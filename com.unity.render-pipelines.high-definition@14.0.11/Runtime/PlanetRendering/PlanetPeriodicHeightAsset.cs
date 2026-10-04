using System;
using System.IO;
using System.Security.Cryptography;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public struct PlanetPeriodicHeightBlob
    {
        public int Width,Height;
        public float TileMetres,HeightScaleMetres,MinimumMetres,MaximumMetres,MaximumSlope;
        public uint4 Identity;
        public BlobArray<float> Samples;
    }

    [CreateAssetMenu(menuName="Rendering/HDRP/Periodic Planet Heightmap")]
    [PreferBinarySerialization]
    public sealed class PlanetPeriodicHeightAsset : ScriptableObject
    {
        [SerializeField] Texture2D sourceTexture;
        public Texture2D NormalSlopes;
        public Texture2D NormalSource;
        public PlanetPeriodicSurfaceSettings SurfaceSettings;
        public int NormalUpChannel=2,NormalUChannel=0,NormalVChannel=1;
        public float NormalGainU=1,NormalGainV=1;
        [SerializeField] int width,height;
        [SerializeField] float tileMetres=5000,heightScaleMetres=2500;
        [SerializeField,HideInInspector] float[] samples;
        [SerializeField,HideInInspector] string digest;
        [NonSerialized] BlobAssetReference<PlanetPeriodicHeightBlob> preview;
        public Texture2D SourceTexture=>sourceTexture;
        public BlobAssetReference<PlanetPeriodicHeightBlob> PreviewBlob
        {get{if(!preview.IsCreated)preview=CreateBlob();return preview;}}

        public void SetData(Texture2D texture,int columns,int rows,float period,float scale,float[] values)
        {
            if(columns<2||rows<2||columns>4096||rows>4096||values==null||values.Length!=columns*rows||
                !math.isfinite(period)||period<=0||!math.isfinite(scale)||scale<=0)
                throw new ArgumentException("Finite metric dimensions and complete float height samples are required.");
            foreach(float value in values)if(!math.isfinite(value))throw new ArgumentException("Height samples must be finite.");
            if(preview.IsCreated)preview.Dispose();
            sourceTexture=texture;width=columns;height=rows;tileMetres=period;heightScaleMetres=scale;samples=(float[])values.Clone();
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
            {
                writer.Write(width);writer.Write(height);writer.Write(tileMetres);writer.Write(heightScaleMetres);
                foreach(float value in samples)writer.Write(value);
                using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","");
            }
        }

        // A baker owns this fresh blob. The editor preview blob remains asset-owned.
        public BlobAssetReference<PlanetPeriodicHeightBlob> CreateBlob()
        {
            if(samples==null||samples.Length!=width*height||width<2||height<2||string.IsNullOrEmpty(digest))
                throw new InvalidOperationException("Periodic height asset has not been imported.");
            using(var builder=new BlobBuilder(Allocator.Temp))
            {
                ref var data=ref builder.ConstructRoot<PlanetPeriodicHeightBlob>();
                data.Width=width;data.Height=height;data.TileMetres=tileMetres;data.HeightScaleMetres=heightScaleMetres;
                data.MinimumMetres=float.PositiveInfinity;data.MaximumMetres=float.NegativeInfinity;
                data.Identity=new uint4(Convert.ToUInt32(digest.Substring(0,8),16),Convert.ToUInt32(digest.Substring(8,8),16),
                    Convert.ToUInt32(digest.Substring(16,8),16),Convert.ToUInt32(digest.Substring(24,8),16));
                var storage=builder.Allocate(ref data.Samples,samples.Length);
                float maximumDelta=0;
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {
                    int i=y*width+x;float value=samples[i];storage[i]=value;
                    data.MinimumMetres=math.min(data.MinimumMetres,value*heightScaleMetres);
                    data.MaximumMetres=math.max(data.MaximumMetres,value*heightScaleMetres);
                    maximumDelta=math.max(maximumDelta,math.abs(value-samples[y*width+(x+1)%width])*width);
                    maximumDelta=math.max(maximumDelta,math.abs(value-samples[((y+1)%height)*width+x])*height);
                }
                data.MaximumSlope=maximumDelta*heightScaleMetres/tileMetres*2;
                return builder.CreateBlobAssetReference<PlanetPeriodicHeightBlob>(Allocator.Persistent);
            }
        }
        void OnDestroy(){if(preview.IsCreated)preview.Dispose();}
    }

    public static class PlanetPeriodicHeight
    {
        public static bool SameSource(in PlanetDefinition a,in PlanetDefinition b)
        {
            if(a.GeneratorVersion!=4&&b.GeneratorVersion!=4)return true;
            return a.PeriodicHeight.IsCreated&&b.PeriodicHeight.IsCreated&&math.all(a.PeriodicHeight.Value.Identity==b.PeriodicHeight.Value.Identity);
        }
        static int Wrap(int index,int count)=>(index%count+count)%count;
        public static float Sample(ref PlanetPeriodicHeightBlob data,double u,double v)
        {
            double px=(u-math.floor(u))*data.Width-.5,py=((1-v)-math.floor(1-v))*data.Height-.5;
            int x=(int)math.floor(px),y=(int)math.floor(py);
            float tx=(float)(px-x),ty=(float)(py-y);
            int x0=Wrap(x,data.Width),x1=Wrap(x+1,data.Width),y0=Wrap(y,data.Height),y1=Wrap(y+1,data.Height);
            float top=math.lerp(data.Samples[y0*data.Width+x0],data.Samples[y0*data.Width+x1],tx);
            float bottom=math.lerp(data.Samples[y1*data.Width+x0],data.Samples[y1*data.Width+x1],tx);
            return math.lerp(top,bottom,ty)*data.HeightScaleMetres;
        }
        public static double Height(in PlanetDefinition definition,double3 direction)
        {
            ref var data=ref definition.PeriodicHeight.Value;
            double3 p=direction*(definition.Radius/data.TileMetres);
            double3 weights=direction*direction;weights*=weights;weights*=weights;weights*=weights;
            weights/=math.csum(weights);
            return weights.x*Sample(ref data,p.z,p.y)+weights.y*Sample(ref data,p.x,p.z)+weights.z*Sample(ref data,p.x,p.y);
        }
    }
}
