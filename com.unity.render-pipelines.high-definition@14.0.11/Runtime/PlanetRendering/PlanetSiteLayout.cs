using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public struct PlanetSiteSettings
    {
        public float Scale, PadSize, HangarWidth, HangarDepth, HangarHeight, TowerHeight;
        public static PlanetSiteSettings Default => new PlanetSiteSettings {Scale=1,PadSize=32,HangarWidth=40,HangarDepth=40,HangarHeight=20,TowerHeight=40};
        public bool IsValid => math.isfinite(Scale) && Scale>=.25f && Scale<=2 &&
            math.isfinite(PadSize) && PadSize>=8 && PadSize<=48 && math.isfinite(HangarWidth) && HangarWidth>=12 && HangarWidth<=48 &&
            math.isfinite(HangarDepth) && HangarDepth>=12 && HangarDepth<=48 && math.isfinite(HangarHeight) && HangarHeight>=4 && HangarHeight<=32 &&
            math.isfinite(TowerHeight) && TowerHeight>=8 && TowerHeight<=60;
    }
    public readonly struct PlanetSitePart
    {
        public readonly string Name;
        public readonly float3 Center,Size;
        public readonly int Material;
        public PlanetSitePart(string name,float3 center,float3 size,int material){Name=name;Center=center;Size=size;Material=material;}
    }
    /// <summary>Metric boxes in the site's double surface frame. No ECS or physics package dependency.</summary>
    public sealed class PlanetSiteLayout
    {
        public PlanetSurfaceFrame Frame {get;private set;}
        public PlanetSitePart[] Parts {get;private set;}
        public static PlanetSiteLayout Build(PlanetDefinition definition,PlanetSurfaceAddress address,PlanetSiteSettings settings)
        {
            if(!settings.IsValid || !PlanetSurfaceCoordinates.TryResolve(definition,address,out var frame))
                throw new ArgumentException("Use a valid generator, surface address, scale 0.25–2 and bounded building dimensions.");
            var parts=new List<PlanetSitePart>(9);float scale=settings.Scale;
            void Box(string name,float2 center,float2 size,float bottom,float top,int material)
            {
                var position=new float3(center.x,(bottom+top)*.5f,center.y);var extent=new float3(size.x,top-bottom,size.y);
                if(!math.all(math.isfinite(position)) || !math.all(math.isfinite(extent)) || math.any(extent<=0) || math.cmax(math.abs(position)+extent*.5f)>500)
                    throw new ArgumentException("Site exceeds its bounded local region. Choose a gentler slope or smaller structures.");
                parts.Add(new PlanetSitePart(name,position,extent,material));
            }
            void Foundation(float2 center,float2 size,out float bottom,out float top)
            {
                double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity;
                int nx=(int)math.ceil(size.x/8),nz=(int)math.ceil(size.y/8);
                for(int z=0;z<=nz;z++)for(int x=0;x<=nx;x++)
                {
                    var local=new double3(center.x+((double)x/nx-.5)*size.x,0,center.y+((double)z/nz-.5)*size.y);
                    var direction=math.normalize(frame.ToPlanet(local));double height=frame.ToLocal(PlanetField.Surface(definition,direction)).y;
                    minimum=math.min(minimum,height);maximum=math.max(maximum,height);
                }
                bottom=(float)minimum-2*scale;top=(float)maximum+scale;
            }
            var padCenter=new float2(0,64)*scale;var padSize=new float2(settings.PadSize)*scale;
            Foundation(padCenter,padSize,out var padBottom,out var padTop);Box("Landing pad",padCenter,padSize,padBottom,padTop,2);
            var hangarCenter=new float2(64,48)*scale;var hangarSize=new float2(settings.HangarWidth,settings.HangarDepth)*scale;
            Foundation(hangarCenter,hangarSize,out var hangarBottom,out var floor);Box("Hangar foundation",hangarCenter,hangarSize,hangarBottom,floor,1);
            float roof=floor+settings.HangarHeight*scale;
            Box("Hangar left wall",hangarCenter+new float2(-(hangarSize.x-scale)*.5f,0),new float2(scale,hangarSize.y),floor,roof,0);
            Box("Hangar right wall",hangarCenter+new float2((hangarSize.x-scale)*.5f,0),new float2(scale,hangarSize.y),floor,roof,0);
            Box("Hangar back wall",hangarCenter+new float2(0,(hangarSize.y-scale)*.5f),new float2(hangarSize.x,scale),floor,roof,0);
            Box("Hangar roof",hangarCenter,hangarSize,roof,roof+scale,0);
            var towerCenter=new float2(-64,72)*scale;var towerSize=new float2(12)*scale;
            Foundation(towerCenter,towerSize,out var towerBottom,out var towerBase);Box("Tower",towerCenter,towerSize,towerBottom,towerBase+settings.TowerHeight*scale,0);
            for(int i=0;i<2;i++)
            {
                var center=new float2(-50-i*24,20)*scale;var size=new float2(16,20)*scale;
                Foundation(center,size,out var bottom,out var top);Box("Habitat "+(i+1),center,size,bottom,top+(10+i*2)*scale,0);
            }
            return new PlanetSiteLayout {Frame=frame,Parts=parts.ToArray()};
        }
        public static Mesh CreateUnitBoxMesh()
        {
            var vertices=new Vector3[24];var normals=new Vector3[24];var triangles=new int[36];
            var axes=new[]{Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
            for(int face=0;face<6;face++)
            {
                var normal=axes[face];var u=Vector3.Cross(Mathf.Abs(normal.y)>.5f?Vector3.forward:Vector3.up,normal)*.5f;
                var v=Vector3.Cross(normal,u);var center=normal*.5f;int first=face*4,t=face*6;
                vertices[first]=center-u-v;vertices[first+1]=center+u-v;vertices[first+2]=center+u+v;vertices[first+3]=center-u+v;
                for(int i=0;i<4;i++)normals[first+i]=normal;
                triangles[t]=first;triangles[t+1]=first+1;triangles[t+2]=first+2;triangles[t+3]=first;triangles[t+4]=first+2;triangles[t+5]=first+3;
            }
            var mesh=new Mesh {name="Planet site unit box"};mesh.vertices=vertices;mesh.normals=normals;mesh.triangles=triangles;mesh.RecalculateBounds();return mesh;
        }
    }
}
