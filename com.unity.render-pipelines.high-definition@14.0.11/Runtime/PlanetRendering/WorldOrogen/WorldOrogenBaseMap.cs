using System;
using System.IO;
using System.Text;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public enum WorldOrogenMapView { Terrain, Satellite, Climate, Heightmap }

    /// <summary>A generated base map, without the preceding generator's runtime detail, masks or deformations.</summary>
    public sealed partial class WorldOrogenBaseMap : IDisposable
    {
        public SurfaceSnapshot Surface {get;private set;}
        public Texture2DArray Colour {get;private set;}
        public SurfaceContentHash ConfigurationDigest {get;private set;}
        public int Resolution {get;private set;}
        public WorldOrogenMapView View {get;private set;}
        public void Dispose(){if(Colour){if(Application.isPlaying)UnityEngine.Object.Destroy(Colour);else UnityEngine.Object.DestroyImmediate(Colour);Colour=null;}}
        public Texture2DArray TakeColour(){var result=Colour;Colour=null;return result;}

        public static int RecommendedResolution(int regions)
        {int result=64;while(result<Math.Sqrt(regions/6.0)*2&&result<1024)result*=2;return result;}

        /// <summary>GPU ray interpolation of the original cell fans. CPU builds only the spatial index and serializes completed samples.</summary>
        public static WorldOrogenBaseMap Create(WorldOrogenGpuState state,WorldOrogenSettings settings,double radius,
            int resolution=0,WorldOrogenMapView view=WorldOrogenMapView.Terrain,ComputeBuffer koppen=null,Func<bool> cancelled=null)
        {
            if(state==null||settings==null)throw new ArgumentNullException();
            if(!settings.Validate(out var error))throw new ArgumentException(error);
            if(!(radius>20000)||!math.isfinite(radius))throw new ArgumentException("A planet radius above 20 km is required for the source's physical height range.");
            if(resolution==0)resolution=RecommendedResolution(state.RegionCount);
            if(!CubeSurface.ValidResolution(resolution)||resolution>2048)throw new ArgumentOutOfRangeException(nameof(resolution));
            if((view==WorldOrogenMapView.Satellite||view==WorldOrogenMapView.Climate)&&koppen==null)
                throw new InvalidOperationException("Calculate climate before choosing this map.");
            Check(cancelled);var source=state.Graph.Source;
            // Bounded before allocation: output float4 + CPU capture/height/colour + immutable clone + index and centers.
            long samples=checked(6L*(resolution+1)*(resolution+1));
            long working=WorkingBytes(state,resolution);
            if(working>4L*1024*1024*1024)throw new InvalidOperationException("Base map exceeds the explicit 4 GiB working budget. Use a smaller map resolution.");
            var shader=Resources.Load<ComputeShader>("WorldOrogenBaseMap");if(!shader)throw new InvalidOperationException("WorldOrogenBaseMap compute resource is unavailable.");
            var index=new SpatialIndex(source.Directions,cancelled);
            var neutralKoppen=new uint[state.RegionCount];
            using(var nodes=WorldOrogenGpuGraph.Upload(index.Nodes,16))
            using(var adjacentTriangles=WorldOrogenGpuGraph.Upload(source.NeighborTriangles,4))
            using(var centers=WorldOrogenGpuGraph.Upload(source.TriangleCenters(),12))
            using(var fallbackKoppen=WorldOrogenGpuGraph.Upload(neutralKoppen,4))
            using(var output=new ComputeBuffer((int)samples,16))
            using(var failures=new ComputeBuffer(1,4))
            {
                failures.SetData(new uint[]{0});int kernel=shader.FindKernel("BuildBaseMap");
                if(!shader.IsSupported(kernel))throw new NotSupportedException("World Orogen base map kernel is not supported on this device.");
                state.Bind(shader,kernel);
                shader.SetBuffer(kernel,"_MapNodes",nodes);shader.SetBuffer(kernel,"_MapNeighborTriangles",adjacentTriangles);
                shader.SetBuffer(kernel,"_MapCenters",centers);shader.SetBuffer(kernel,"_MapKoppen",koppen??fallbackKoppen);
                shader.SetBuffer(kernel,"_MapOutput",output);shader.SetBuffer(kernel,"_MapFailures",failures);
                shader.SetInt("_MapRoot",index.Root);shader.SetInt("_MapResolution",resolution);shader.SetInt("_MapView",(int)view);
                shader.Dispatch(kernel,(resolution+8)/8,(resolution+8)/8,6);Check(cancelled);
                var missing=new uint[1];failures.GetData(missing);Check(cancelled);
                var values=new float4[(int)samples];output.GetData(values);Check(cancelled);
                if(missing[0]!=0)
                {
                    var details=new StringBuilder();int count=0,side=resolution+1,faceSamples=side*side;
                    for(int i=0;i<values.Length&&count<8;i++)if(values[i].x<0){details.Append($" face{i/faceSamples}/{i%side}/{i/side%side}:nearest{values[i].y};");count++;}
                    throw new InvalidOperationException($"Source cell fan reprojection left {missing[0]} uncovered/nonfinite samples.{details} No partial map was published.");
                }
                return Capture(values,resolution,settings,radius,view,cancelled);
            }
        }

        static long WorkingBytes(WorldOrogenGpuState state,int resolution)
        {
            var graph=state.Graph.Source;long samples=checked(6L*(resolution+1)*(resolution+1));
            // Include the retained CPU graph, CPU+GPU spatial nodes, simultaneous triangle-center
            // upload arrays, texture/readback buffers and the immutable snapshot's height copies.
            return checked(state.EstimatedBytes+graph.EstimatedBytes+samples*52+graph.RegionCount*44L+graph.TriangleCount*24L+graph.SideCount*4L);
        }
        static SurfaceSnapshot CaptureSurface(float4[] values,int resolution,WorldOrogenSettings settings,double radius,WorldOrogenMapView view,Func<bool> cancelled)
        {
            int side=resolution+1,faceSamples=side*side;
            // One canonical owner at every cube edge/corner. Reprojection uses the same float input direction,
            // but explicit ownership also guarantees bit-identical saved vertices across GPU implementations.
            for(int face=0;face<6;face++)for(int y=0;y<=resolution;y++)for(int x=0;x<=resolution;x++)
            {
                if(x!=0&&y!=0&&x!=resolution&&y!=resolution)continue;
                var key=new SurfaceTileKey(face,0,0,0);CubeSurface.TrySampleDirection(key,resolution,x,y,out var d);
                CubeSurface.TryLocate(d,0,out var owner,out var uv);
                int ox=(int)Math.Round(uv.x*resolution),oy=(int)Math.Round(uv.y*resolution);
                values[face*faceSamples+y*side+x]=values[owner.Face*faceSamples+oy*side+ox];
            }
            var tiles=new SurfaceTileData[6];double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity;
            for(int face=0;face<6;face++)
            {
                Check(cancelled);var heights=new float[faceSamples];
                for(int i=0;i<heights.Length;i++){heights[i]=values[face*faceSamples+i].w;minimum=Math.Min(minimum,heights[i]);maximum=Math.Max(maximum,heights[i]);}
                tiles[face]=new SurfaceTileData(new SurfaceTileKey(face,0,0,0),resolution,heights);
            }
            var recipe=new SurfaceRecipe(settings.Seed,SurfaceStyle.EarthLike,radius,minimum,maximum);
            SurfaceContentHash baseDigest;
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
            {writer.Write("WorldOrogen/canonical-cell-fans/v1");writer.Write(settings.ConfigurationDigest().ToBytes());writer.Write(radius);writer.Write(resolution);writer.Write((int)view);foreach(var tile in tiles)writer.Write(tile.ContentHash.ToBytes());writer.Flush();baseDigest=SurfaceContentHash.Compute(stream.ToArray());}
            return new SurfaceSnapshot(recipe,new SurfaceRevision(SurfaceHashing.Recipe(recipe),baseDigest,1),0,resolution,tiles,SurfaceDetailRecipe.Disabled);
        }
        static WorldOrogenBaseMap Capture(float4[] values,int resolution,WorldOrogenSettings settings,double radius,WorldOrogenMapView view,Func<bool> cancelled)
        {
            var snapshot=CaptureSurface(values,resolution,settings,radius,view,cancelled);int side=resolution+1,faceSamples=side*side;
            var texture=new Texture2DArray(side,side,6,TextureFormat.RGBA32,false,true){name="World Orogen "+view,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            try
            {
                for(int face=0;face<6;face++)
                {Check(cancelled);var colors=new Color32[faceSamples];for(int i=0;i<colors.Length;i++){var c=values[face*faceSamples+i];colors[i]=new Color(c.x,c.y,c.z,1);}texture.SetPixels32(colors,face);}
                texture.Apply(false,false);
                return new WorldOrogenBaseMap{Surface=snapshot,Colour=texture,ConfigurationDigest=settings.ConfigurationDigest(),Resolution=resolution,View=view};
            }
            catch{UnityEngine.Object.DestroyImmediate(texture);throw;}
        }
        static void Check(Func<bool> cancelled){if(cancelled?.Invoke()==true)throw new OperationCanceledException("Base map cancelled before publication.");}

        sealed class SpatialIndex
        {
            readonly float3[] points;readonly int[] order;readonly Func<bool> cancelled;int next;
            internal readonly int4[] Nodes;internal int Root {get;}
            internal SpatialIndex(float3[] points,Func<bool> cancelled)
            {this.points=points;this.cancelled=cancelled;order=new int[points.Length];Nodes=new int4[points.Length];for(int i=0;i<order.Length;i++)order[i]=i;Root=Build(0,order.Length,0);}
            int Compare(int a,int b,int axis){int c=points[a][axis].CompareTo(points[b][axis]);return c!=0?c:a.CompareTo(b);}
            void Swap(int a,int b){int v=order[a];order[a]=order[b];order[b]=v;}
            void Select(int start,int end,int target,int axis)
            {
                while(end-start>1)
                {Check(cancelled);int pivot=(start+end)/2;Swap(pivot,end-1);int value=order[end-1],at=start;for(int i=start;i<end-1;i++)if(Compare(order[i],value,axis)<0)Swap(i,at++);Swap(at,end-1);if(at==target)return;if(target<at)end=at;else start=at+1;}
            }
            int Build(int start,int end,int depth)
            {if(start>=end)return -1;Check(cancelled);int axis=depth%3,mid=(start+end)/2;Select(start,end,mid,axis);int node=next++;int region=order[mid],left=Build(start,mid,depth+1),right=Build(mid+1,end,depth+1);Nodes[node]=new int4(region,left,right,axis);return node;}
        }
    }
}
