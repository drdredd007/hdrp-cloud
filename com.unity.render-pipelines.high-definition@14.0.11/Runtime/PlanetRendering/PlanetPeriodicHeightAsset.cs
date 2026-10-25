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
        public float DetailFrequency,DetailHeightFraction,MaximumMacroAngularSlope;
        public uint4 Identity;
        public BlobArray<float> Samples;
        public int PlainsSide,MacroSide;
        public BlobArray<float> Plains;
        public BlobArray<float2> Macro;
    }

    [CreateAssetMenu(menuName="Rendering/HDRP/Periodic Planet Heightmap")]
    [PreferBinarySerialization]
    public sealed class PlanetPeriodicHeightAsset : ScriptableObject
    {
        [SerializeField] Texture2D sourceTexture;
        public Texture2D NormalSlopes;
        public Texture2D PlainsSlopes;
        public Texture2DArray MacroNormals;
        [SerializeField,HideInInspector] int plainsSide,macroSide;
        [SerializeField,HideInInspector] float[] plains,macro;
        public Texture2D NormalSource;
        public PlanetPeriodicSurfaceSettings SurfaceSettings;
        public PlanetPeriodicMaterialSettings MaterialSettings;
        public int NormalUpChannel=2,NormalUChannel=0,NormalVChannel=1;
        public float NormalGainU=1,NormalGainV=1;
        [SerializeField] int width,height;
        [SerializeField] float tileMetres=5000,heightScaleMetres=2500;
        [SerializeField] float detailFrequency,detailHeightFraction;
        [SerializeField,HideInInspector] float[] samples;
        [SerializeField,HideInInspector] string digest;
        [NonSerialized] BlobAssetReference<PlanetPeriodicHeightBlob> preview;
        public Texture2D SourceTexture=>sourceTexture;
        public string ContentDigest=>digest;
        public BlobAssetReference<PlanetPeriodicHeightBlob> PreviewBlob
        {get{if(!preview.IsCreated)preview=CreateBlob();return preview;}}

        public void SetData(Texture2D texture,int columns,int rows,float period,float scale,float[] values,float frequency=0,float heightFraction=0)
        {
            if(columns<2||rows<2||columns>4096||rows>4096||values==null||values.Length!=columns*rows||
                !math.isfinite(period)||period<=0||!math.isfinite(scale)||scale<=0||
                !math.isfinite(frequency)||frequency<0||frequency>256||!math.isfinite(heightFraction)||heightFraction<0||heightFraction>1||
                (heightFraction>0&&frequency<1))
                throw new ArgumentException("Finite metric dimensions and complete float height samples are required.");
            foreach(float value in values)if(!math.isfinite(value))throw new ArgumentException("Height samples must be finite.");
            if(preview.IsCreated)preview.Dispose();
            sourceTexture=texture;width=columns;height=rows;tileMetres=period;heightScaleMetres=scale;samples=(float[])values.Clone();
            detailFrequency=frequency;detailHeightFraction=heightFraction;
            plainsSide=macroSide=0;plains=null;macro=null;PlainsSlopes=null;MacroNormals=null;
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
            {
                writer.Write(width);writer.Write(height);writer.Write(tileMetres);writer.Write(heightScaleMetres);
                foreach(float value in samples)writer.Write(value);
                if(heightFraction>0){writer.Write(frequency);writer.Write(heightFraction);}
                using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","");
            }
        }

        public void SetGeography(int plainSide,float[] plainHeights,int faceSide,float[] geography)
        {
            if(samples==null||string.IsNullOrEmpty(digest))throw new InvalidOperationException("Import the periodic source before geography.");
            if(plainSide<2||plainSide>4096||plainHeights==null||plainHeights.Length!=plainSide*plainSide||
                faceSide<3||faceSide>1025||geography==null||geography.Length!=6*faceSide*faceSide*2)
                throw new ArgumentException("Complete plains and six nodal geography faces are required.");
            foreach(float f in plainHeights)if(!math.isfinite(f))throw new ArgumentException("Nonfinite plains height.");
            for(int i=0;i<geography.Length;i++)if(!math.isfinite(geography[i])||(i%2==1&&(geography[i]<0||geography[i]>1)))
                throw new ArgumentException("Invalid geography height or weight.");
            if(preview.IsCreated)preview.Dispose();
            plainsSide=plainSide;macroSide=faceSide;plains=(float[])plainHeights.Clone();macro=(float[])geography.Clone();
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){
                writer.Write(width);writer.Write(height);writer.Write(tileMetres);writer.Write(heightScaleMetres);
                foreach(float f in samples)writer.Write(f);writer.Write(detailFrequency);writer.Write(detailHeightFraction);writer.Write(plainSide);writer.Write(faceSide);
                foreach(float f in plains)writer.Write(f);foreach(float f in macro)writer.Write(f);
                using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","");
            }
        }
        public void BindGeography(MaterialPropertyBlock block)
        {
            bool enabled=macroSide>0&&MacroNormals&&PlainsSlopes;
            block.SetFloat("_PeriodicMacroSide",enabled?macroSide:0);
            if(enabled){block.SetTexture("_PeriodicMacroNormals",MacroNormals);block.SetTexture("_PeriodicPlainsSlopes",PlainsSlopes);}
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
                data.DetailFrequency=detailFrequency;data.DetailHeightFraction=detailHeightFraction;
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
                if(detailHeightFraction>0){data.MinimumMetres*=1+detailHeightFraction;data.MaximumMetres*=1+detailHeightFraction;
                    data.MaximumSlope*=1+detailFrequency*detailHeightFraction;}
                data.PlainsSide=plainsSide;data.MacroSide=macroSide;
                var ps=builder.Allocate(ref data.Plains,plainsSide>0?plains.Length:0);
                var ms=builder.Allocate(ref data.Macro,macroSide>0?macro.Length/2:0);
                if(macroSide>0){
                    float pmin=float.PositiveInfinity,pmax=float.NegativeInfinity,mmin=float.PositiveInfinity,mmax=float.NegativeInfinity;
                    for(int i=0;i<plains.Length;i++){ps[i]=plains[i];pmin=math.min(pmin,plains[i]);pmax=math.max(pmax,plains[i]);}
                    for(int i=0;i<ms.Length;i++){ms[i]=new float2(macro[i*2],macro[i*2+1]);mmin=math.min(mmin,ms[i].x);mmax=math.max(mmax,ms[i].x);}
                    data.MinimumMetres=math.min(data.MinimumMetres,pmin)+mmin;
                    data.MaximumMetres=math.max(data.MaximumMetres,pmax)+mmax;
                    float plainDelta=0;
                    for(int y=0;y<plainsSide;y++)for(int x=0;x<plainsSide;x++){
                        float v=plains[y*plainsSide+x];
                        plainDelta=math.max(plainDelta,math.abs(v-plains[y*plainsSide+(x+1)%plainsSide]));
                        plainDelta=math.max(plainDelta,math.abs(v-plains[((y+1)%plainsSide)*plainsSide+x]));
                    }
                    data.MaximumSlope=math.max(data.MaximumSlope,plainDelta*plainsSide/tileMetres*2);
                    float2 delta=0;
                    for(int f=0;f<6;f++)for(int y=0;y<macroSide;y++)for(int x=0;x<macroSide;x++){
                        int i=f*macroSide*macroSide+y*macroSide+x;
                        if(x+1<macroSide)delta=math.max(delta,math.abs(ms[i]-ms[i+1]));
                        if(y+1<macroSide)delta=math.max(delta,math.abs(ms[i]-ms[i+macroSide]));
                    }
                    // Cube-chart Jacobian bound; converted to metric slope using the body's radius at selection time.
                    data.MaximumMacroAngularSlope=4*(macroSide-1)*(delta.x+delta.y*(data.MaximumMetres-data.MinimumMetres));
                }
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
        static float SamplePlain(ref PlanetPeriodicHeightBlob data,double u,double v)
        {
            int n=data.PlainsSide;double px=(u-math.floor(u))*n-.5,py=((1-v)-math.floor(1-v))*n-.5;
            int x=(int)math.floor(px),y=(int)math.floor(py);float tx=(float)(px-x),ty=(float)(py-y);
            int x0=Wrap(x,n),x1=Wrap(x+1,n),y0=Wrap(y,n),y1=Wrap(y+1,n);
            return math.lerp(math.lerp(data.Plains[y0*n+x0],data.Plains[y0*n+x1],tx),math.lerp(data.Plains[y1*n+x0],data.Plains[y1*n+x1],tx),ty);
        }
        public static float2 Geography(ref PlanetPeriodicHeightBlob data,double3 d)
        {
            double3 a=math.abs(d);int face;double u,v;
            if(a.x>=a.y&&a.x>=a.z){face=d.x>=0?0:1;u=(d.x>=0?-d.z:d.z)/a.x;v=d.y/a.x;}
            else if(a.y>=a.z){face=d.y>=0?2:3;u=d.x/a.y;v=(d.y>=0?-d.z:d.z)/a.y;}
            else{face=d.z>=0?4:5;u=(d.z>=0?d.x:-d.x)/a.z;v=d.y/a.z;}
            int n=data.MacroSide;double px=math.clamp((u*.5+.5)*(n-1),0,n-1),py=math.clamp((v*.5+.5)*(n-1),0,n-1);
            int x=math.min((int)px,n-2),y=math.min((int)py,n-2),at=face*n*n+y*n+x;
            return math.lerp(math.lerp(data.Macro[at],data.Macro[at+1],(float)(px-x)),math.lerp(data.Macro[at+n],data.Macro[at+n+1],(float)(px-x)),(float)(py-y));
        }
        public static double Height(in PlanetDefinition definition,double3 direction)
        {
            ref var data=ref definition.PeriodicHeight.Value;
            double3 p=direction*(definition.Radius/data.TileMetres);
            double3 weights=direction*direction;weights*=weights;weights*=weights;weights*=weights;
            weights/=math.csum(weights);
            double h=weights.x*Sample(ref data,p.z,p.y)+weights.y*Sample(ref data,p.x,p.z)+weights.z*Sample(ref data,p.x,p.y);
            if(data.DetailHeightFraction>0){p=p*data.DetailFrequency+new double3(.173,.391,.719);
                h+=data.DetailHeightFraction*(weights.x*Sample(ref data,p.z,p.y)+weights.y*Sample(ref data,p.x,p.z)+weights.z*Sample(ref data,p.x,p.y));}
            if(data.MacroSide>0){
                var macro=Geography(ref data,direction);
                double plain=weights.x*SamplePlain(ref data,direction.z*definition.Radius/data.TileMetres,direction.y*definition.Radius/data.TileMetres)+
                    weights.y*SamplePlain(ref data,direction.x*definition.Radius/data.TileMetres,direction.z*definition.Radius/data.TileMetres)+
                    weights.z*SamplePlain(ref data,direction.x*definition.Radius/data.TileMetres,direction.y*definition.Radius/data.TileMetres);
                h=macro.x+math.lerp(plain,h,macro.y);
            }
            return h;
        }
    }
}
