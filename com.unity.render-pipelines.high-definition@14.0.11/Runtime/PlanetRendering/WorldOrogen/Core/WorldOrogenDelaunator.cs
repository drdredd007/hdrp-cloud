// Delaunator 5.0.1 algorithm port: https://github.com/mapbox/delaunator/tree/v5.0.1
// Copyright (c) 2021, Mapbox, ISC license (see WorldOrogen/THIRD_PARTY_NOTICES.txt).
// Robust orientation uses an exact binary-integer fallback instead of importing robust-predicates.
using System;
using System.Numerics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>The upstream advancing-hull Delaunay algorithm, including original stable distance quicksort.</summary>
    internal sealed class WorldOrogenDelaunator
    {
        readonly double[] coordinates,distances;
        readonly int[] triangles,halfedges,hullPrev,hullNext,hullTri,hullHash,ids,edgeStack=new int[512];
        readonly int hashSize;
        readonly Func<bool> cancelled;
        int hullStart,length;
        double centerX,centerY;
        public int[] Triangles {get;private set;}
        public int[] Halfedges {get;private set;}
        public WorldOrogenDelaunator(double[] values,Func<bool> cancelled=null)
        {
            coordinates=values??throw new ArgumentNullException(nameof(values));this.cancelled=cancelled;int n=values.Length/2;
            if(values.Length%2!=0||n<3)throw new ArgumentException("Delaunay requires at least three planar points.");
            triangles=new int[checked(Math.Max(2*n-5,0)*3)];halfedges=new int[triangles.Length];hashSize=(int)Math.Ceiling(Math.Sqrt(n));
            hullPrev=new int[n];hullNext=new int[n];hullTri=new int[n];hullHash=new int[hashSize];ids=new int[n];distances=new double[n];Update();
        }
        void Update()
        {
            int n=ids.Length;double minX=double.PositiveInfinity,minY=double.PositiveInfinity,maxX=double.NegativeInfinity,maxY=double.NegativeInfinity;
            for(int i=0;i<n;i++){double x=coordinates[i*2],y=coordinates[i*2+1];minX=Math.Min(x,minX);minY=Math.Min(y,minY);maxX=Math.Max(x,maxX);maxY=Math.Max(y,maxY);ids[i]=i;}
            double cx=(minX+maxX)/2,cy=(minY+maxY)/2,best=double.PositiveInfinity;int i0=0,i1=0,i2=0;
            for(int i=0;i<n;i++){double d=Distance(cx,cy,coordinates[i*2],coordinates[i*2+1]);if(d<best){i0=i;best=d;}}
            double x0=coordinates[i0*2],y0=coordinates[i0*2+1];best=double.PositiveInfinity;
            for(int i=0;i<n;i++){if(i==i0)continue;double d=Distance(x0,y0,coordinates[i*2],coordinates[i*2+1]);if(d<best&&d>0){i1=i;best=d;}}
            double x1=coordinates[i1*2],y1=coordinates[i1*2+1];best=double.PositiveInfinity;
            for(int i=0;i<n;i++){if(i==i0||i==i1)continue;double r=Radius(x0,y0,x1,y1,coordinates[i*2],coordinates[i*2+1]);if(r<best){i2=i;best=r;}}
            if(double.IsInfinity(best))throw new ArgumentException("Collinear or coincident sphere projection.");
            double x2=coordinates[i2*2],y2=coordinates[i2*2+1];if(Orientation(x0,y0,x1,y1,x2,y2)<0)
            {int ti=i1;i1=i2;i2=ti;double tx=x1,ty=y1;x1=x2;y1=y2;x2=tx;y2=ty;}
            Circumcenter(x0,y0,x1,y1,x2,y2,out centerX,out centerY);
            for(int i=0;i<n;i++)distances[i]=Distance(coordinates[i*2],coordinates[i*2+1],centerX,centerY);
            Quicksort(0,n-1);hullStart=i0;int hullSize=3;
            hullNext[i0]=hullPrev[i2]=i1;hullNext[i1]=hullPrev[i0]=i2;hullNext[i2]=hullPrev[i1]=i0;hullTri[i0]=0;hullTri[i1]=1;hullTri[i2]=2;
            for(int i=0;i<hashSize;i++)hullHash[i]=-1;hullHash[Hash(x0,y0)]=i0;hullHash[Hash(x1,y1)]=i1;hullHash[Hash(x2,y2)]=i2;AddTriangle(i0,i1,i2,-1,-1,-1);
            double xp=0,yp=0;const double epsilon=2.2204460492503130808472633361816e-16;
            for(int k=0;k<n;k++)
            {
                if((k&1023)==0&&cancelled!=null&&cancelled())throw new OperationCanceledException();
                int i=ids[k];double x=coordinates[i*2],y=coordinates[i*2+1];if(k>0&&Math.Abs(x-xp)<=epsilon&&Math.Abs(y-yp)<=epsilon)continue;xp=x;yp=y;
                if(i==i0||i==i1||i==i2)continue;int start=0,key=Hash(x,y);
                for(int j=0;j<hashSize;j++){start=hullHash[(key+j)%hashSize];if(start!=-1&&start!=hullNext[start])break;}
                if(start<0)throw new InvalidOperationException("Delaunay hull hash is empty.");start=hullPrev[start];int e=start,q;
                while(true){q=hullNext[e];if(Orientation(x,y,coordinates[e*2],coordinates[e*2+1],coordinates[q*2],coordinates[q*2+1])<0)break;e=q;if(e==start){e=-1;break;}}
                if(e==-1)continue;int t=AddTriangle(e,i,hullNext[e],-1,-1,hullTri[e]);hullTri[i]=Legalize(t+2);hullTri[e]=t;hullSize++;
                int next=hullNext[e];while(true)
                {q=hullNext[next];if(Orientation(x,y,coordinates[next*2],coordinates[next*2+1],coordinates[q*2],coordinates[q*2+1])>=0)break;t=AddTriangle(next,i,q,hullTri[i],-1,hullTri[next]);hullTri[i]=Legalize(t+2);hullNext[next]=next;hullSize--;next=q;}
                if(e==start)while(true)
                {q=hullPrev[e];if(Orientation(x,y,coordinates[q*2],coordinates[q*2+1],coordinates[e*2],coordinates[e*2+1])>=0)break;t=AddTriangle(q,i,e,-1,hullTri[e],hullTri[q]);Legalize(t+2);hullTri[q]=t;hullNext[e]=e;hullSize--;e=q;}
                hullStart=hullPrev[i]=e;hullNext[e]=hullPrev[next]=i;hullNext[i]=next;hullHash[Hash(x,y)]=i;hullHash[Hash(coordinates[e*2],coordinates[e*2+1])]=e;
            }
            Triangles=new int[length];Halfedges=new int[length];Array.Copy(triangles,Triangles,length);Array.Copy(halfedges,Halfedges,length);
        }
        int Hash(double x,double y){double dx=x-centerX,dy=y-centerY,p=dx/(Math.Abs(dx)+Math.Abs(dy));return (int)Math.Floor((dy>0?3-p:1+p)/4*hashSize)%hashSize;}
        int Legalize(int a)
        {
            int at=0,ar=0;while(true)
            {
                int b=halfedges[a],a0=a-a%3;ar=a0+(a+2)%3;if(b==-1){if(at==0)break;a=edgeStack[--at];continue;}
                int b0=b-b%3,al=a0+(a+1)%3,bl=b0+(b+2)%3,p0=triangles[ar],pr=triangles[a],pl=triangles[al],p1=triangles[bl];
                if(InCircle(coordinates[p0*2],coordinates[p0*2+1],coordinates[pr*2],coordinates[pr*2+1],coordinates[pl*2],coordinates[pl*2+1],coordinates[p1*2],coordinates[p1*2+1]))
                {
                    triangles[a]=p1;triangles[b]=p0;int opposite=halfedges[bl];if(opposite==-1)
                    {int e=hullStart;do{if(hullTri[e]==bl){hullTri[e]=a;break;}e=hullPrev[e];}while(e!=hullStart);}
                    Link(a,opposite);Link(b,halfedges[ar]);Link(ar,bl);int br=b0+(b+1)%3;if(at<edgeStack.Length)edgeStack[at++]=br;else throw new InvalidOperationException("Delaunay legalization exceeds upstream 512-edge stack.");
                }
                else{if(at==0)break;a=edgeStack[--at];}
            }return ar;
        }
        void Link(int a,int b){halfedges[a]=b;if(b!=-1)halfedges[b]=a;}
        int AddTriangle(int i0,int i1,int i2,int a,int b,int c){int t=length;triangles[t]=i0;triangles[t+1]=i1;triangles[t+2]=i2;Link(t,a);Link(t+1,b);Link(t+2,c);length+=3;return t;}
        static double Distance(double ax,double ay,double bx,double by){double dx=ax-bx,dy=ay-by;return dx*dx+dy*dy;}
        static bool InCircle(double ax,double ay,double bx,double by,double cx,double cy,double px,double py)
        {double dx=ax-px,dy=ay-py,ex=bx-px,ey=by-py,fx=cx-px,fy=cy-py,ap=dx*dx+dy*dy,bp=ex*ex+ey*ey,cp=fx*fx+fy*fy;return dx*(ey*cp-bp*fy)-dy*(ex*cp-bp*fx)+ap*(ex*fy-ey*fx)<0;}
        static double Radius(double ax,double ay,double bx,double by,double cx,double cy)
        {double dx=bx-ax,dy=by-ay,ex=cx-ax,ey=cy-ay,bl=dx*dx+dy*dy,cl=ex*ex+ey*ey,d=.5/(dx*ey-dy*ex),x=(ey*bl-dy*cl)*d,y=(dx*cl-ex*bl)*d;return x*x+y*y;}
        static void Circumcenter(double ax,double ay,double bx,double by,double cx,double cy,out double x,out double y)
        {double dx=bx-ax,dy=by-ay,ex=cx-ax,ey=cy-ay,bl=dx*dx+dy*dy,cl=ex*ex+ey*ey,d=.5/(dx*ey-dy*ex);x=ax+(ey*bl-dy*cl)*d;y=ay+(dx*cl-ex*bl)*d;}
        static double Orientation(double ax,double ay,double bx,double by,double cx,double cy)
        {
            double left=(ay-cy)*(bx-cx),right=(ax-cx)*(by-cy),det=left-right;
            if(Math.Abs(det)>3.3306690738754716e-16*Math.Abs(left+right))return det;
            var values=new[]{ax,ay,bx,by,cx,cy};int minExponent=int.MaxValue;for(int i=0;i<values.Length;i++)if(values[i]!=0)minExponent=Math.Min(minExponent,Exponent(values[i]));
            if(minExponent==int.MaxValue)return 0;var exact=new BigInteger[6];for(int i=0;i<6;i++)exact[i]=Integer(values[i],minExponent);
            return ((exact[1]-exact[5])*(exact[2]-exact[4])-(exact[0]-exact[4])*(exact[3]-exact[5])).Sign;
        }
        static int Exponent(double value){long bits=BitConverter.DoubleToInt64Bits(value);int e=(int)((bits>>52)&2047);return e==0?-1074:e-1075;}
        static BigInteger Integer(double value,int exponent)
        {long bits=BitConverter.DoubleToInt64Bits(value);ulong fraction=(ulong)bits&0x000fffffffffffffUL;int e=(int)((bits>>52)&2047);BigInteger n=e==0?fraction:fraction|(1UL<<52);if(bits<0)n=-n;return n<<(Exponent(value)-exponent);}
        void Swap(int a,int b){int t=ids[a];ids[a]=ids[b];ids[b]=t;}
        void Quicksort(int left,int right)
        {
            if(right-left<=20){for(int i=left+1;i<=right;i++){int temporary=ids[i],j=i-1;double d=distances[temporary];while(j>=left&&distances[ids[j]]>d)ids[j+1]=ids[j--];ids[j+1]=temporary;}}
            else{int median=(left+right)>>1,i=left+1,j=right;Swap(median,i);if(distances[ids[left]]>distances[ids[right]])Swap(left,right);if(distances[ids[i]]>distances[ids[right]])Swap(i,right);if(distances[ids[left]]>distances[ids[i]])Swap(left,i);int temporary=ids[i];double d=distances[temporary];while(true){do{i++;}while(distances[ids[i]]<d);do{j--;}while(distances[ids[j]]>d);if(j<i)break;Swap(i,j);}ids[left+1]=ids[j];ids[j]=temporary;if(right-i+1>=j-left){Quicksort(i,right);Quicksort(left,j-1);}else{Quicksort(left,j-1);Quicksort(i,right);}}
        }
    }
    public static class WorldOrogenTopology
    {
        public static WorldOrogenGraph Build(int detail,double irregularity,WorldOrogenRandom random,Func<bool> cancelled=null)
        {var directions=WorldOrogenGraph.Fibonacci(detail,irregularity,random,cancelled);var delaunay=new WorldOrogenDelaunator(WorldOrogenGraph.Stereographic(directions),cancelled);return WorldOrogenGraph.ClosePole(directions,delaunay.Triangles,delaunay.Halfedges);}
    }
}
