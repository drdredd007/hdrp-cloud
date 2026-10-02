using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Conservative fixed-grid collection. Neither render LOD nor the current surface revision affects these addresses.</summary>
    public static class SurfaceScatterCells
    {
        public const int MaximumCollectedCells=1024*1024;
        struct Rectangle {public int Face,X0,Y0,X1,Y1;}
        public static bool TryCollect(double3 unitAnchor,double angularRadius,int fixedLevel,int maximumCells,List<SurfaceTileKey> output,out SurfaceSampleStatus status)
        {
            status=SurfaceSampleStatus.InvalidInput;
            if(output==null||!CubeSurface.TryNormalize(unitAnchor,out var anchor)||!math.isfinite(angularRadius)||angularRadius<0||angularRadius>Math.PI||fixedLevel<0||fixedLevel>SurfaceTileKey.MaximumLevel||maximumCells<1||maximumCells>MaximumCollectedCells)return false;
            double chord=2*math.sin(angularRadius*.5);var lower=math.max(new double3(-1),anchor-chord);var upper=math.min(new double3(1),anchor+chord);
            var rectangles=new Rectangle[6];int rectangleCount=0;long count=0;int grid=(int)(1L<<fixedLevel);
            for(int face=0;face<6;face++)
            {
                FaceBounds(face,lower,upper,out double d0,out double d1,out double2 n0,out double2 n1,out double otherMinimum);
                if(d1<=0||d1<otherMinimum)continue;
                d0=math.max(d0,otherMinimum);
                double2 a0,a1;
                if(d0<=0){a0=new double2(-1);a1=new double2(1);}
                else
                {
                    var p00=n0/d0;var p01=n0/d1;var p10=n1/d0;var p11=n1/d1;
                    a0=math.max(new double2(-1),math.min(math.min(p00,p01),math.min(p10,p11)));
                    a1=math.min(new double2(1),math.max(math.max(p00,p01),math.max(p10,p11)));
                }
                if(math.any(a0>a1))continue;
                var lo=(int2)math.min(grid-1,math.floor(math.max(new double2(0),(a0+1)*.5)*grid));
                var hi=(int2)math.min(grid-1,math.floor(math.min(new double2(1),(a1+1)*.5)*grid));
                count=checked(count+((long)hi.x-lo.x+1)*((long)hi.y-lo.y+1));
                if(count>maximumCells){status=SurfaceSampleStatus.NotReady;return false;}
                rectangles[rectangleCount++]=new Rectangle{Face=face,X0=lo.x,Y0=lo.y,X1=hi.x,Y1=hi.y};
            }
            // The caller's list is unchanged on invalid or over-budget input; sample-count-sized allocations happen only after preflight.
            output.Clear();if(output.Capacity<count)output.Capacity=(int)count;
            for(int i=0;i<rectangleCount;i++){var r=rectangles[i];for(int y=r.Y0;y<=r.Y1;y++)for(int x=r.X0;x<=r.X1;x++)output.Add(new SurfaceTileKey(r.Face,fixedLevel,x,y));}
            status=SurfaceSampleStatus.Ready;return true;
        }
        static double MinimumAbs(double lo,double hi)=>lo<=0&&hi>=0?0:math.min(math.abs(lo),math.abs(hi));
        internal static void FaceBounds(int face,double3 lo,double3 hi,out double d0,out double d1,out double2 n0,out double2 n1,out double otherMinimum)
        {
            int axis=face/2;double sign=(face&1)==0?1:-1;
            d0=sign>0?lo[axis]:-hi[axis];d1=sign>0?hi[axis]:-lo[axis];
            int p=(axis+1)%3,q=(axis+2)%3;otherMinimum=math.max(MinimumAbs(lo[p],hi[p]),MinimumAbs(lo[q],hi[q]));
            switch(face)
            {
                case 0:n0=new double2(-hi.z,lo.y);n1=new double2(-lo.z,hi.y);break;
                case 1:n0=new double2(lo.z,lo.y);n1=new double2(hi.z,hi.y);break;
                case 2:n0=new double2(lo.x,-hi.z);n1=new double2(hi.x,-lo.z);break;
                case 3:n0=new double2(lo.x,lo.z);n1=new double2(hi.x,hi.z);break;
                case 4:n0=new double2(lo.x,lo.y);n1=new double2(hi.x,hi.y);break;
                default:n0=new double2(-hi.x,lo.y);n1=new double2(-lo.x,hi.y);break;
            }
        }
    }
}
