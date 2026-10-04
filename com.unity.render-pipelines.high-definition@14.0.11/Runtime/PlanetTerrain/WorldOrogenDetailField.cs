using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Optional final-map conditioning. Flow is spill-aware reference-area metadata, not simulated water discharge.
    /// Environment precipitation/cold are neutral when captured from a retained map without source climate.</summary>
    public sealed class WorldOrogenDetailField
    {
        public const int CurrentVersion=1;
        readonly float4[] geometry,flow,environment;
        readonly WorldOrogenDetailRangeLevel[] rangeLevels;
        readonly float2[] ranges;
        public WorldOrogenDetailRecipe Recipe {get;}
        public double SourceRadius {get;}
        public double Radius=>SourceRadius;
        public double SeaLevel {get;}
        public SurfaceContentHash SourceBaseDigest {get;}
        public SurfaceContentHash ContentDigest {get;}
        public int Version=>CurrentVersion;
        public int Resolution {get;}
        public double MaximumAmplitude {get;}
        public int SampleCount=>geometry.Length;
        public IReadOnlyList<WorldOrogenDetailRangeLevel> RangeLevels {get;}
        public long EstimatedResidentBytes=>checked((long)geometry.Length*48+(long)ranges.Length*8+(long)rangeLevels.Length*12);
        public WorldOrogenDetailField(WorldOrogenDetailRecipe recipe,double sourceRadius,double seaLevel,SurfaceContentHash sourceBaseDigest,
            int resolution,float4[] geometry,float4[] flow,float4[] environment)
        {
            if(!recipe.IsValid||!math.isfinite(sourceRadius)||sourceRadius<=0||!math.isfinite(seaLevel)||seaLevel<=-sourceRadius||!sourceBaseDigest.IsValid||
                resolution!=recipe.ConditioningResolution)throw new ArgumentException("Invalid World Orogen detail metadata.");
            int count=checked(6*(resolution+1)*(resolution+1));
            if(geometry==null||flow==null||environment==null||geometry.Length!=count||flow.Length!=count||environment.Length!=count)
                throw new ArgumentException("World Orogen conditioning requires three complete six-face node grids.");
            Recipe=recipe;SourceRadius=sourceRadius;SeaLevel=seaLevel;SourceBaseDigest=sourceBaseDigest;Resolution=resolution;
            this.geometry=(float4[])geometry.Clone();this.flow=(float4[])flow.Clone();this.environment=(float4[])environment.Clone();
            double maximum=0;
            for(int i=0;i<count;i++)
            {
                var g=this.geometry[i];var f=this.flow[i];var e=this.environment[i];
                if(!math.all(math.isfinite(g))||g.y>g.x||g.z<g.x||g.w<0||g.w>recipe.MaximumAmplitudeMetres+1e-4||
                    !math.all(math.isfinite(f))||math.lengthsq(f.xyz)>1.00001f||f.w<0||f.w>1||
                    !math.all(math.isfinite(e))||math.any(e<0)||math.any(e>1))throw new ArgumentException("Invalid conditioning values.");
                maximum=math.max(maximum,g.w);CanonicalZero(ref this.geometry[i]);CanonicalZero(ref this.flow[i]);CanonicalZero(ref this.environment[i]);
            }
            ValidateSharedNodes();MaximumAmplitude=maximum*recipe.Strength;
            var headers=new List<WorldOrogenDetailRangeLevel>();var values=new List<float2>();int n=resolution,level=0;
            while(true)
            {
                int offset=values.Count;headers.Add(new WorldOrogenDetailRangeLevel(level,n,offset));
                for(int face=0;face<6;face++)for(int y=0;y<n;y++)for(int x=0;x<n;x++)
                {
                    float lo=float.PositiveInfinity,hi=0;
                    if(level==0)
                    {
                        for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++){float a=this.geometry[NodeIndex(resolution,face,x+dx,y+dy)].w;lo=math.min(lo,a);hi=math.max(hi,a);}
                    }
                    else
                    {
                        var previous=headers[level-1];int pn=previous.Resolution;
                        for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++){var a=values[previous.Offset+face*pn*pn+(2*y+dy)*pn+2*x+dx];lo=math.min(lo,a.x);hi=math.max(hi,a.y);}
                    }
                    values.Add(new float2(lo,hi));
                }
                if(n==1)break;n>>=1;level++;
            }
            rangeLevels=headers.ToArray();ranges=values.ToArray();RangeLevels=Array.AsReadOnly(rangeLevels);
            ContentDigest=SurfaceHashing.Compute(w=>{w.Write(CurrentVersion);SurfaceHashing.WriteHash(w,recipe.ContentDigest);SurfaceHashing.WriteHash(w,sourceBaseDigest);
                w.Write(sourceRadius);w.Write(seaLevel);w.Write(resolution);SurfaceHashing.WriteAttributes(w,this.geometry);SurfaceHashing.WriteAttributes(w,this.flow);SurfaceHashing.WriteAttributes(w,this.environment);});
        }
        static void CanonicalZero(ref float4 v){if(v.x==0)v.x=0;if(v.y==0)v.y=0;if(v.z==0)v.z=0;if(v.w==0)v.w=0;}
        void ValidateSharedNodes()
        {
            for(int face=0;face<6;face++)for(int y=0;y<=Resolution;y++)for(int x=0;x<=Resolution;x++)
            {
                if(x!=0&&y!=0&&x!=Resolution&&y!=Resolution)continue;
                int own=WorldOrogenDetailGrid.CanonicalNode(Resolution,face,x,y),id=NodeIndex(Resolution,face,x,y);
                if(!math.all(this.geometry[id]==this.geometry[own])||!math.all(this.flow[id]==this.flow[own])||!math.all(this.environment[id]==this.environment[own]))
                    throw new ArgumentException("Shared conditioning edge/corner nodes must have identical canonical values.");
            }
        }
        public static int NodeIndex(int resolution,int face,int x,int y)=>face*(resolution+1)*(resolution+1)+y*(resolution+1)+x;
        public float4 GeometryAt(int index)=>geometry[index];public float4 FlowAt(int index)=>flow[index];public float4 EnvironmentAt(int index)=>environment[index];
        public float2 RangeAt(int index)=>ranges[index];public int RangeCount=>ranges.Length;
        public float4[] CopyGeometry()=>(float4[])geometry.Clone();public float4[] CopyFlow()=>(float4[])flow.Clone();public float4[] CopyEnvironment()=>(float4[])environment.Clone();
    }

    /// <summary>Integer cube ownership. Dominant-axis x/y/z priority matches CubeSurface.TryLocate exactly.</summary>
    public static class WorldOrogenDetailGrid
    {
        public static int CanonicalNode(int resolution,int face,int x,int y)
        {
            if(resolution<1||face<0||face>=6||x<0||x>resolution||y<0||y>resolution)throw new ArgumentException("Invalid cube node.");
            int a=2*x-resolution,b=2*y-resolution;int3 p;
            switch(face){case 0:p=new int3(resolution,b,-a);break;case 1:p=new int3(-resolution,b,a);break;case 2:p=new int3(a,resolution,-b);break;
                case 3:p=new int3(a,-resolution,b);break;case 4:p=new int3(a,b,resolution);break;default:p=new int3(-a,b,-resolution);break;}
            int f,u,v;var q=math.abs(p);
            if(q.x>=q.y&&q.x>=q.z){if(p.x>0){f=0;u=-p.z;v=p.y;}else{f=1;u=p.z;v=p.y;}}
            else if(q.y>=q.z){if(p.y>0){f=2;u=p.x;v=-p.z;}else{f=3;u=p.x;v=p.z;}}
            else if(p.z>0){f=4;u=p.x;v=p.y;}else{f=5;u=-p.x;v=p.y;}
            return WorldOrogenDetailField.NodeIndex(resolution,f,(u+resolution)/2,(v+resolution)/2);
        }
    }
}
