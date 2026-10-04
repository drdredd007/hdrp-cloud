// Discrete edifice catalogs from js/elevation.js cc2662b4, GPL-3.0-only.
// Sequential seed/spacing decisions are CPU metadata. Every per-region uplift stays in compute.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
namespace UnityEngine.Rendering.HighDefinition
{
    [StructLayout(LayoutKind.Sequential)]public struct WorldOrogenVolcano {public double3 Position;public double Height,Sigma,InverseSigmaSquared;}
    [StructLayout(LayoutKind.Sequential)]public struct WorldOrogenDome
    {public double3 Position,U,V;public double Strength,Sigma,CosPeak,CosSwell,InvPeak,InvSwell,SwellStrength,Stretch,CalderaDepth,InvCaldera,Age;public double3 RiftAngles;public uint RiftCount,HasCaldera;}
    [StructLayout(LayoutKind.Sequential)]public struct WorldOrogenLip {public double3 Position,U,V;public double Sigma,Height,Aspect;}
    public sealed class WorldOrogenEdifices
    {
        public readonly WorldOrogenDome[] Domes;
        public readonly WorldOrogenLip[] Lips;
        public readonly int2[] DomeRanges;
        public readonly int[] DomeReferences;
        static double Dot(double3 a,double3 b)=>(a.x*b.x+a.y*b.y)+a.z*b.z;
        static double Length(double3 x)=>Math.Sqrt(Dot(x,x));
        static double3 Unit(double3 x){double n=Length(x);return x/(n==0?1:n);}
        static void Frame(double3 p,double3 drift,out double3 u,out double3 v){u=Unit(drift-Dot(drift,p)*p);v=math.cross(p,u);}
        static int Nearest(WorldOrogenGraph graph,double3 p,Func<bool> cancelled)
        {int best=0;double maximum=-2;for(int r=0;r<graph.RegionCount;r++){if((r&16383)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();double d=Dot(p,(double3)graph.Directions[r]);if(d>maximum){maximum=d;best=r;}}return best;}
        sealed class DomeBuilder {public double3 P,Drift,U,V;public double Strength,BaseStrength,Sigma;public int Chain,Length;public bool Continental;public double[] Rifts;}
        public WorldOrogenEdifices(WorldOrogenGraph graph,WorldOrogenPlateSet plates,float[] mantle,uint[] ocean,double seed,Func<bool> cancelled=null)
        {
            var rng=new WorldOrogenRandom(seed+999);var positionRng=new WorldOrogenRandom(seed+1001);var shapeNoise=new WorldOrogenNoise(seed+503);
            var domes=new List<DomeBuilder>();var lips=new List<WorldOrogenLip>();
            double[] Rifts(double angle,int index,int length)=>index==0?new[]{angle,angle+Math.PI*.6,angle-Math.PI*.6}:index==1?new[]{angle,angle+Math.PI}:index<=(int)Math.Floor(length*.4)?new[]{angle}:Array.Empty<double>();
            DomeBuilder AddDome(double3 p,double3 drift,double strength,double baseStrength,double sigma,int index,int length,bool continental,double[] rifts)
            {Frame(p,drift,out var u,out var v);var d=new DomeBuilder{P=p,Drift=drift,U=u,V=v,Strength=strength,BaseStrength=baseStrength,Sigma=sigma,Chain=index,Length=length,Continental=continental,Rifts=rifts};domes.Add(d);return d;}
            void Satellites(DomeBuilder parent)
            {
                for(int s=0;s<2;s++)
                {double angle=rng.Next()*2*Math.PI,offset=parent.Sigma*.8*(.5+rng.Next()*.5);double3 direction=Math.Cos(angle)*parent.U+Math.Sin(angle)*parent.V;double3 p=Unit(parent.P*Math.Cos(offset)+direction*Math.Sin(offset));AddDome(p,parent.Drift,parent.Strength*.35,parent.BaseStrength*.35,parent.Sigma*.5,parent.Chain,parent.Length,false,Array.Empty<double>());}
            }
            for(int h=0;h<5;h++)
            {
                if(cancelled?.Invoke()==true)throw new OperationCanceledException();
                double strength=.6*(.4+rng.Next()*1.2),sigma=.006*(.4+rng.Next()*1.2),decay=.65+(rng.Next()-.5)*.35;int length=Math.Max(3,6+(int)Math.Floor((rng.Next()-.5)*10+.5));double3 position=default;
                if(mantle!=null)
                {double best=double.NegativeInfinity;for(int c=0;c<8;c++){double theta=2*Math.PI*positionRng.Next(),cos=2*positionRng.Next()-1,sin=Math.Sqrt(1-cos*cos);var p=new double3(sin*Math.Cos(theta),sin*Math.Sin(theta),cos);int r=Nearest(graph,p,cancelled);double score=mantle[r]+(positionRng.Next()-.5)*.3;if(score>best){best=score;position=p;}}}
                else {double theta=2*Math.PI*positionRng.Next(),cos=2*positionRng.Next()-1,sin=Math.Sqrt(1-cos*cos);position=new double3(sin*Math.Cos(theta),sin*Math.Sin(theta),cos);}
                int center=Nearest(graph,position,cancelled),plate=plates.RegionPlate[center];double4 motion=plates.Motion[plate];double3 drift=motion.w*math.cross(motion.xyz,position);double driftLength=Length(drift);if(driftLength<1e-6)continue;drift/=driftLength;
                bool continental=!plates.Ocean[plate];double sigmaScale=continental?2.5:1,strengthScale=continental?.4:1,oceanBoost=continental?1:1.8,effectiveSigma=sigma*sigmaScale,effectiveStrength=strength*strengthScale*oceanBoost;
                double riftAngle=shapeNoise.Noise(position.x*10,position.y*10,position.z*10)*Math.PI;
                var first=AddDome(position,drift,effectiveStrength,strength*strengthScale,effectiveSigma,0,length,continental,Rifts(riftAngle,0,length));Satellites(first);
                double3 perpendicular=Unit(math.cross(drift,position)),current=position;double str=effectiveStrength,baseStr=strength*strengthScale;
                for(int c=0;c<length;c++)
                {
                    int index=c+1;double decayJitter=decay*(.7+rng.Next()*.6);str*=decayJitter;baseStr*=decayJitter;
                    double spacing=.06*(.3+rng.Next()*1.4),ageBroadening=1+index*.03,stepSigma=effectiveSigma*(.5+rng.Next())*ageBroadening,wobble=(rng.Next()-.5)*.8;
                    double3 d=-drift+perpendicular*wobble,tangent=d-Dot(d,current)*current;double n=Length(tangent);if(n<1e-6)break;tangent/=n;current=Unit(current*Math.Cos(spacing)+tangent*Math.Sin(spacing));
                    var parent=AddDome(current,drift,str,baseStr,stepSigma,index,length,continental,Rifts(riftAngle,index,length));if(index<=(int)Math.Ceiling(length*.4))Satellites(parent);
                }
                int lipRegion=Nearest(graph,current,cancelled);double up=mantle!=null?Math.Max(0,mantle[lipRegion]):.5,landBoost=ocean[lipRegion]!=0?.6:1;
                double lipStrength=.03*(.5+rng.Next())*(.5+up)*landBoost,lipSigma=.08*(.7+.6*rng.Next());Frame(current,drift,out var lipU,out var lipV);double aspect=1.5+rng.Next()*1.5;
                lips.Add(new WorldOrogenLip{Position=current,U=lipU,V=lipV,Sigma=lipSigma,Height=lipStrength,Aspect=aspect});
                for(int lobe=0;lobe<6;lobe++)
                {
                    double angle=rng.Next()*2*Math.PI,distance=lipSigma*.6*(.4+rng.Next()*.6);double3 offset=Math.Cos(angle)*lipU+Math.Sin(angle)*lipV,p=Unit(current*Math.Cos(distance)+offset*Math.Sin(distance));double a=rng.Next()*Math.PI,ca=Math.Cos(a),sa=Math.Sin(a);
                    double lobeSigma=lipSigma*.6*(.6+rng.Next()*.8),lobeHeight=lipStrength*.9*(.5+rng.Next()*.5),lobeAspect=1.2+rng.Next()*1.3;
                    lips.Add(new WorldOrogenLip{Position=p,U=ca*lipU+sa*lipV,V=-sa*lipU+ca*lipV,Sigma=lobeSigma,Height=lobeHeight,Aspect=lobeAspect});
                }
            }
            var captured=new WorldOrogenDome[domes.Count];
            for(int i=0;i<domes.Count;i++)
            {
                var d=domes[i];double swellSigma=d.Sigma*2*(d.Continental?1.5:1),calSigma=d.Sigma*(d.Continental?.35:.25);double3 angles=0;for(int a=0;a<d.Rifts.Length;a++)angles[a]=d.Rifts[a];
                captured[i]=new WorldOrogenDome{Position=d.P,U=d.U,V=d.V,Strength=d.Strength,Sigma=d.Sigma,CosPeak=Math.Cos(d.Sigma*5.5),InvPeak=-.5/(d.Sigma*d.Sigma),SwellStrength=d.BaseStrength*.1,CosSwell=Math.Cos(swellSigma*3),InvSwell=-.5/(swellSigma*swellSigma),Stretch=1/1.05,HasCaldera=d.Chain<=1&&d.Strength>.15?1u:0u,CalderaDepth=d.Strength*(d.Continental?.3:.2),InvCaldera=-.5/(calSigma*calSigma),Age=d.Length>0?d.Chain/(double)d.Length:0,RiftAngles=angles,RiftCount=(uint)d.Rifts.Length};
            }
            Domes=captured;Lips=lips.ToArray();BuildGrid(Array.ConvertAll(Domes,d=>d.Position),18,36,out DomeRanges,out DomeReferences);
        }
        internal static void BuildGrid(double3[] positions,int rows,int columns,out int2[] ranges,out int[] references)
        {
            ranges=new int2[rows*columns];var bins=new List<int>[ranges.Length];for(int i=0;i<positions.Length;i++){var p=positions[i];double lat=Math.Asin(math.clamp(p.y,-1,1)),lon=Math.Atan2(p.x,p.z);int bi=math.clamp((int)Math.Floor((lat+Math.PI/2)/Math.PI*rows),0,rows-1),bj=math.clamp((int)Math.Floor((lon+Math.PI)/(2*Math.PI)*columns),0,columns-1),bin=bi*columns+bj;if(bins[bin]==null)bins[bin]=new List<int>();bins[bin].Add(i);}var packed=new List<int>();for(int i=0;i<bins.Length;i++){ranges[i]=new int2(packed.Count,bins[i]?.Count??0);if(bins[i]!=null)packed.AddRange(bins[i]);}references=packed.ToArray();
        }
        public static void IslandArcTopology(WorldOrogenGraph graph,WorldOrogenPlateSet plates,WorldOrogenTectonicFields t,uint[] ocean,double[] gpuMacroScore,out float[] distance,out float[] strength,Func<bool> cancelled=null)
        {
            int width=Math.Max(5,(int)Math.Floor(7*t.Scale+.5));distance=new float[graph.RegionCount];Array.Fill(distance,width+1);strength=new float[graph.RegionCount];var candidates=new List<int>();
            for(int r=0;r<graph.RegionCount;r++)if(t.Boundary[r]==1&&t.BothOcean[r]!=0&&t.Subduct[r]<.45&&gpuMacroScore[r]>=.55)candidates.Add(r);
            candidates.Sort((a,b)=>{int c=gpuMacroScore[b].CompareTo(gpuMacroScore[a]);return c==0?a.CompareTo(b):c;});var origins=new List<int>();
            foreach(int r in candidates){bool close=false;foreach(int o in origins){double3 d=(double3)graph.Directions[r]-(double3)graph.Directions[o];if(Dot(d,d)<.25){close=true;break;}}if(close)continue;origins.Add(r);if(origins.Count==5)break;}
            var queue=new List<int>(origins);foreach(int r in origins){distance[r]=0;strength[r]=(float)Math.Min(1,t.Stress[r]/t.MaximumStress);}
            for(int head=0;head<queue.Count;head++){if((head&4095)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();int r=queue[head];float next=distance[r]+1;if(next>width)continue;for(int i=graph.Offsets[r];i<graph.Offsets[r+1];i++){int nb=graph.Neighbors[i];if(next<distance[nb]&&plates.RegionPlate[r]==plates.RegionPlate[nb]&&ocean[nb]!=0){distance[nb]=next;strength[nb]=strength[r];queue.Add(nb);}}}
        }
        public static WorldOrogenVolcano[] VolcanoCatalog(WorldOrogenGraph graph,WorldOrogenTectonicFields t,double3[] gpuScoreHeightSigma,Func<bool> cancelled=null)
        {
            var candidates=new List<int>();for(int r=0;r<graph.RegionCount;r++)if(t.Boundary[r]==1&&t.HasOcean[r]!=0&&t.Subduct[r]<.45)candidates.Add(r);
            candidates.Sort((a,b)=>{int c=gpuScoreHeightSigma[b].x.CompareTo(gpuScoreHeightSigma[a].x);return c==0?a.CompareTo(b):c;});var chosen=new List<WorldOrogenVolcano>();
            foreach(int r in candidates){if((chosen.Count&255)==0&&cancelled?.Invoke()==true)throw new OperationCanceledException();double3 p=(double3)graph.Directions[r];bool close=false;foreach(var v in chosen)if(Math.Max(0,2*(1-Dot(p,v.Position)))<.015*.015){close=true;break;}if(close)continue;double3 data=gpuScoreHeightSigma[r];chosen.Add(new WorldOrogenVolcano{Position=p,Height=data.y,Sigma=data.z,InverseSigmaSquared=-.5/(data.z*data.z)});}return chosen.ToArray();
        }
    }
}
