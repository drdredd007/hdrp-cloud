using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace SpaceRunner.PlanetTerrain
{
    public readonly struct NativeSurfaceLandformView
    {
        public readonly double Radius,MinimumHeight,MaximumHeight;
        public readonly SurfaceContentHash ContentDigest;
        public readonly NativeArray<SurfaceLandformControl>.ReadOnly Controls;
        public readonly NativeArray<SurfaceLandformChannel>.ReadOnly Channels;
        public readonly NativeArray<SurfaceLandformDivide>.ReadOnly Divides;
        public readonly NativeArray<SurfaceLandformCell>.ReadOnly Cells;
        public readonly NativeArray<int>.ReadOnly CellDivides,References;
        public readonly NativeArray<SurfaceDrainageIndexNode>.ReadOnly Index;
        public bool Enabled=>ContentDigest.IsValid;
        internal NativeSurfaceLandformView(SurfaceLandformField field,NativeArray<SurfaceLandformControl> controls,
            NativeArray<SurfaceLandformChannel> channels,NativeArray<SurfaceLandformDivide> divides,NativeArray<SurfaceLandformCell> cells,
            NativeArray<int> cellDivides,NativeArray<SurfaceDrainageIndexNode> index,NativeArray<int> references)
        {
            Radius=field?.SourceRecipe.Radius??0;MinimumHeight=field?.MinimumHeight??0;MaximumHeight=field?.MaximumHeight??0;ContentDigest=field?.ContentDigest??default;
            Controls=controls.AsReadOnly();Channels=channels.AsReadOnly();Divides=divides.AsReadOnly();Cells=cells.AsReadOnly();
            CellDivides=cellDivides.AsReadOnly();Index=index.AsReadOnly();References=references.AsReadOnly();
        }
    }
    public sealed class NativeSurfaceLandformData:IDisposable
    {
        readonly SurfaceLandformField field;bool disposed;
        NativeArray<SurfaceLandformControl> controls;
        NativeArray<SurfaceLandformChannel> channels;
        NativeArray<SurfaceLandformDivide> divides;
        NativeArray<SurfaceLandformCell> cells;
        NativeArray<int> cellDivides,references;
        NativeArray<SurfaceDrainageIndexNode> index;
        public NativeSurfaceLandformView View=>!disposed?new NativeSurfaceLandformView(field,controls,channels,divides,cells,cellDivides,index,references):throw new ObjectDisposedException(nameof(NativeSurfaceLandformData));
        public NativeSurfaceLandformData(SurfaceLandformField field,Allocator allocator)
            :this(field,allocator,true) { }

        // Quadrature reads only the captured height controls and their index. This private worker
        // copy is never published as the full graph closure used by ordinary physical consumers.
        internal static NativeSurfaceLandformData CreateHeightOnly(SurfaceLandformField field,Allocator allocator)
        {
            _=HeightOnlyBytes(field); // Checked admission arithmetic before any native allocation.
            return new NativeSurfaceLandformData(field,allocator,false);
        }
        internal static long HeightOnlyBytes(SurfaceLandformField field)
        {
            if(field==null)throw new ArgumentNullException(nameof(field));
            return checked((long)field.Controls.Count*UnsafeUtility.SizeOf<SurfaceLandformControl>()+
                (long)field.IndexNodeCount*UnsafeUtility.SizeOf<SurfaceDrainageIndexNode>()+
                (long)field.SpatialReferenceCount*UnsafeUtility.SizeOf<int>());
        }
        NativeSurfaceLandformData(SurfaceLandformField field,Allocator allocator,bool includeTopology)
        {
            if(allocator==Allocator.None||allocator==Allocator.Invalid)throw new ArgumentException("Landform copies require an owning allocator.");
            this.field=field;
            try
            {
                controls=new NativeArray<SurfaceLandformControl>(field?.Controls.Count??0,allocator);channels=new NativeArray<SurfaceLandformChannel>(includeTopology?(field?.Channels.Count??0):0,allocator);
                divides=new NativeArray<SurfaceLandformDivide>(includeTopology?(field?.Divides.Count??0):0,allocator);cells=new NativeArray<SurfaceLandformCell>(includeTopology?(field?.Cells.Count??0):0,allocator);
                cellDivides=new NativeArray<int>(includeTopology?(field?.CellDivideReferenceCount??0):0,allocator);index=new NativeArray<SurfaceDrainageIndexNode>(field?.IndexNodeCount??0,allocator);references=new NativeArray<int>(field?.SpatialReferenceCount??0,allocator);
                if(field==null)return;
                for(int i=0;i<controls.Length;i++)controls[i]=field.Controls[i];for(int i=0;i<channels.Length;i++)channels[i]=field.Channels[i];
                for(int i=0;i<divides.Length;i++)divides[i]=field.Divides[i];for(int i=0;i<cells.Length;i++)cells[i]=field.Cells[i];
                for(int i=0;i<cellDivides.Length;i++)cellDivides[i]=field.CellDivideAt(i);for(int i=0;i<index.Length;i++)index[i]=field.IndexNodeAt(i);for(int i=0;i<references.Length;i++)references[i]=field.SpatialReferenceAt(i);
            }
            catch{Dispose();throw;}
        }
        public void Dispose()
        {
            if(disposed)return;if(controls.IsCreated)controls.Dispose();if(channels.IsCreated)channels.Dispose();if(divides.IsCreated)divides.Dispose();if(cells.IsCreated)cells.Dispose();
            if(cellDivides.IsCreated)cellDivides.Dispose();if(index.IsCreated)index.Dispose();if(references.IsCreated)references.Dispose();disposed=true;
        }
        public JobHandle Dispose(JobHandle readers)
        {
            if(disposed)return readers;var result=readers;
            if(controls.IsCreated)result=JobHandle.CombineDependencies(result,controls.Dispose(readers));if(channels.IsCreated)result=JobHandle.CombineDependencies(result,channels.Dispose(readers));
            if(divides.IsCreated)result=JobHandle.CombineDependencies(result,divides.Dispose(readers));if(cells.IsCreated)result=JobHandle.CombineDependencies(result,cells.Dispose(readers));
            if(cellDivides.IsCreated)result=JobHandle.CombineDependencies(result,cellDivides.Dispose(readers));if(index.IsCreated)result=JobHandle.CombineDependencies(result,index.Dispose(readers));if(references.IsCreated)result=JobHandle.CombineDependencies(result,references.Dispose(readers));
            disposed=true;return result;
        }
    }
}
