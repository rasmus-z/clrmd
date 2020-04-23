﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

#pragma warning disable CA1721 // Property names should not match get methods

namespace Microsoft.Diagnostics.Runtime.Implementation
{
    public sealed class ClrmdHeap : ClrHeap
    {
        private const int MaxGen2ObjectSize = 85000;
        private readonly IHeapHelpers _helpers;

        private readonly object _sync = new object();

        private volatile VolatileHeapData? _volatileHeapData;

        [ThreadStatic]
        private static HeapWalkStep[]? _steps;

        [ThreadStatic]
        private static int _step = -1;

        /// <summary>
        /// This is a circular buffer of steps.
        /// </summary>
        public static IReadOnlyList<HeapWalkStep>? Steps => _steps;

        /// <summary>
        /// Gets the current index into the Steps circular buffer.
        /// </summary>
        public static int Step => _step;

        /// <summary>
        /// Turns on heap walk logging.
        /// </summary>
        /// <param name="bufferSize">The number of entries in the heap walk buffer.</param>
        public static void LogHeapWalkSteps(int bufferSize)
        {
            _step = bufferSize - 1;
            if (_steps is null || _steps.Length != bufferSize)
                _steps = new HeapWalkStep[bufferSize];
        }

        public override ClrRuntime Runtime { get; }

        public override bool CanWalkHeap { get; }


        private Dictionary<ulong, ulong> AllocationContext => GetHeapData().AllocationContext;

        private ImmutableArray<FinalizerQueueSegment> FQRoots => GetHeapData().FQRoots;

        private ImmutableArray<FinalizerQueueSegment> FQObjects => GetHeapData().FQObjects;

        public override ImmutableArray<ClrSegment> Segments => GetHeapData().Segments;

        public override int LogicalHeapCount { get; }

        public override ClrType FreeType { get; }

        public override ClrType StringType { get; }

        public override ClrType ObjectType { get; }

        public override ClrType ExceptionType { get; }

        public override bool IsServer { get; }

        public ClrmdHeap(ClrRuntime runtime, IHeapData data)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));

            _helpers = data.HeapHelpers;

            Runtime = runtime;
            CanWalkHeap = data.CanWalkHeap;
            IsServer = data.IsServer;
            LogicalHeapCount = data.LogicalHeapCount;

            // Prepopulate a few important method tables.  This should never fail.
            FreeType = _helpers.Factory.CreateSystemType(this, data.FreeMethodTable, "Free");
            ObjectType = _helpers.Factory.CreateSystemType(this, data.ObjectMethodTable, "System.Object");
            StringType = _helpers.Factory.CreateSystemType(this, data.StringMethodTable, "System.String");
            ExceptionType = _helpers.Factory.CreateSystemType(this, data.ExceptionMethodTable, "System.Exception");
        }

        private VolatileHeapData GetHeapData()
        {
            VolatileHeapData? data = _volatileHeapData;
            if (data != null)
                return data;

            lock (_sync)
            {
                data = _volatileHeapData;
                if (data != null)
                    return data;

                data = new VolatileHeapData(this, _helpers);
                _volatileHeapData = data;
                return data;
            }
        }

        public void ClearCachedData()
        {
            _volatileHeapData = null;
        }

        public ulong SkipAllocationContext(ClrSegment seg, ulong obj, ulong mt, Action<ulong, ulong, int, int, uint>? callback)
        {
            if (seg is null)
                throw new ArgumentNullException(nameof(seg));

            if (seg.IsLargeObjectSegment)
                return obj;

            uint minObjSize = (uint)IntPtr.Size * 3;
            while (AllocationContext.TryGetValue(obj, out ulong nextObj))
            {
                nextObj += Align(minObjSize, seg.IsLargeObjectSegment);

                if (obj >= nextObj || obj >= seg.End)
                    return 0;

                // Only if there's data corruption:
                if (obj >= nextObj || obj >= seg.End)
                {
                    callback?.Invoke(obj, mt, int.MinValue + 2, -1, 0);
                    return 0;
                }

                obj = nextObj;
            }

            return obj;
        }

        private static void WriteHeapStep(ulong obj, ulong mt, int baseSize, int componentSize, uint count)
        {
            if (_steps is null)
                return;

            _step = (_step + 1) % _steps.Length;
            _steps[_step] = new HeapWalkStep
            {
                Address = obj,
                MethodTable = mt,
                BaseSize = baseSize,
                ComponentSize = componentSize,
                Count = count
            };
        }

        public override IEnumerable<ClrObject> EnumerateObjects() => Segments.SelectMany(s => s.EnumerateObjects());

        internal static ulong Align(ulong size, bool large)
        {
            ulong AlignConst;
            ulong AlignLargeConst = 7;

            if (IntPtr.Size == 4)
                AlignConst = 3;
            else
                AlignConst = 7;

            if (large)
                return (size + AlignLargeConst) & ~AlignLargeConst;

            return (size + AlignConst) & ~AlignConst;
        }

        public override ClrType? GetObjectType(ulong objRef)
        {
            ulong mt = _helpers.DataReader.ReadPointer(objRef);

            if (mt == 0)
                return null;

            return _helpers.Factory.GetOrCreateType(mt, objRef);
        }

        public override ClrSegment? GetSegmentByAddress(ulong objRef)
        {
            VolatileHeapData data = GetHeapData();
            ImmutableArray<ClrSegment> segments = data.Segments;
            if (segments.Length == 0)
                return null;

            if (segments[0].FirstObjectAddress <= objRef && objRef < segments[segments.Length - 1].End)
            {
                // Start the segment search where you where last
                int prevIndex = data.LastSegmentIndex;
                int curIdx = prevIndex;
                for (; ; )
                {
                    ClrSegment segment = segments[curIdx];
                    unchecked
                    {
                        long offsetInSegment = (long)(objRef - segment.Start);
                        if (offsetInSegment >= 0)
                        {
                            long intOffsetInSegment = offsetInSegment;
                            if (intOffsetInSegment < (long)segment.Length)
                            {
                                data.LastSegmentIndex = curIdx;
                                return segment;
                            }
                        }
                    }

                    // Get the next segment loop until you come back to where you started.
                    curIdx++;
                    if (curIdx >= segments.Length)
                        curIdx = 0;
                    if (curIdx == prevIndex)
                        break;
                }
            }

            return null;
        }

        public override ulong GetObjectSize(ulong objRef, ClrType type)
        {
            ulong size;
            if (type.ComponentSize == 0)
            {
                size = (uint)type.StaticSize;
            }
            else
            {
                uint countOffset = (uint)IntPtr.Size;
                ulong loc = objRef + countOffset;

                uint count = _helpers.DataReader.Read<uint>(loc);

                // Strings in v4+ contain a trailing null terminator not accounted for.
                if (StringType == type)
                    count++;

                size = count * (ulong)type.ComponentSize + (ulong)type.StaticSize;
            }

            uint minSize = (uint)IntPtr.Size * 3;
            if (size < minSize)
                size = minSize;
            return size;
        }

        public override IEnumerable<ClrObject> EnumerateObjectReferences(ulong obj, ClrType type, bool carefully, bool considerDependantHandles)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));

            if (considerDependantHandles)
            {
                ImmutableArray<(ulong Source, ulong Target)> dependent = GetHeapData().GetDependentHandles(_helpers);

                if (dependent.Length > 0)
                {
                    int index = dependent.Search(obj, (x, y) => x.Source.CompareTo(y));
                    if (index != -1)
                    {
                        while (index >= 1 && dependent[index - 1].Source == obj)
                            index--;

                        while (index < dependent.Length && dependent[index].Source == obj)
                        {
                            ulong dependantObj = dependent[index++].Target;
                            yield return new ClrObject(dependantObj, GetObjectType(dependantObj));
                        }
                    }
                }
            }

            if (type.IsCollectible)
            {
                ulong la = _helpers.DataReader.ReadPointer(type.LoaderAllocatorHandle);
                if (la != 0)
                    yield return new ClrObject(la, GetObjectType(la));
            }

            if (type.ContainsPointers)
            {
                GCDesc gcdesc = type.GCDesc;
                if (!gcdesc.IsEmpty)
                {
                    ulong size = GetObjectSize(obj, type);
                    if (carefully)
                    {
                        ClrSegment? seg = GetSegmentByAddress(obj);
                        if (seg is null || obj + size > seg.End || (!seg.IsLargeObjectSegment && size > MaxGen2ObjectSize))
                            yield break;
                    }

                    foreach ((ulong reference, int offset) in gcdesc.WalkObject(obj, size, _helpers.DataReader))
                        yield return new ClrObject(reference, GetObjectType(reference));
                }
            }
        }

        public override IEnumerable<ClrReference> EnumerateReferencesWithFields(ulong obj, ClrType type, bool carefully, bool considerDependantHandles)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));

            if (considerDependantHandles)
            {
                ImmutableArray<(ulong Source, ulong Target)> dependent = GetHeapData().GetDependentHandles(_helpers);

                if (dependent.Length > 0)
                {
                    int index = dependent.Search(obj, (x, y) => x.Source.CompareTo(y));
                    if (index != -1)
                    {
                        while (index >= 1 && dependent[index - 1].Source == obj)
                            index--;

                        while (index < dependent.Length && dependent[index].Source == obj)
                        {
                            ulong dependantObj = dependent[index++].Target;
                            ClrObject target = new ClrObject(dependantObj, GetObjectType(dependantObj));
                            yield return ClrReference.CreateFromDependentHandle(target);
                        }
                    }
                }
            }

            if (type.ContainsPointers)
            {
                GCDesc gcdesc = type.GCDesc;
                if (!gcdesc.IsEmpty)
                {
                    ulong size = GetObjectSize(obj, type);
                    if (carefully)
                    {
                        ClrSegment? seg = GetSegmentByAddress(obj);
                        if (seg is null || obj + size > seg.End || (!seg.IsLargeObjectSegment && size > MaxGen2ObjectSize))
                            yield break;
                    }

                    foreach ((ulong reference, int offset) in gcdesc.WalkObject(obj, size, _helpers.DataReader))
                    {
                        ClrObject target = new ClrObject(reference, GetObjectType(reference));
                        yield return ClrReference.CreateFromFieldOrArray(target, type, offset);
                    }
                }
            }
        }


        public override IEnumerable<IClrRoot> EnumerateRoots()
        {
            // Handle table
            foreach (ClrHandle handle in Runtime.EnumerateHandles())
                if (handle.IsStrong)
                    yield return handle;

            // Finalization Queue
            foreach (ClrFinalizerRoot root in EnumerateFinalizerRoots())
                yield return root;

            // Threads
            foreach (IClrRoot root in EnumerateStackRoots())
                yield return root;
        }

        private IEnumerable<IClrRoot> EnumerateStackRoots()
        {
            foreach (ClrThread thread in Runtime.Threads)
            {
                if (thread.IsAlive)
                {
                    foreach (IClrRoot root in thread.EnumerateStackRoots())
                        yield return root;
                }
            }
        }

        public override IEnumerable<ClrObject> EnumerateFinalizableObjects() => EnumerateFQ(FQObjects).Select(root => root.Object);

        public override IEnumerable<ClrFinalizerRoot> EnumerateFinalizerRoots() => EnumerateFQ(FQRoots);

        private IEnumerable<ClrFinalizerRoot> EnumerateFQ(IEnumerable<FinalizerQueueSegment> fqList)
        {
            if (fqList is null)
                yield break;

            foreach (FinalizerQueueSegment seg in fqList)
            {
                for (ulong ptr = seg.Start; ptr < seg.End; ptr += (uint)IntPtr.Size)
                {
                    ulong obj = _helpers.DataReader.ReadPointer(ptr);
                    if (obj == 0)
                        continue;

                    ulong mt = _helpers.DataReader.ReadPointer(obj);
                    ClrType? type = _helpers.Factory.GetOrCreateType(mt, obj);
                    if (type != null)
                        yield return new ClrFinalizerRoot(ptr, new ClrObject(obj, type));
                }
            }
        }
    }

    class VolatileHeapData
    {
        private ImmutableArray<(ulong Source, ulong Target)> _dependentHandles;

        public ImmutableArray<FinalizerQueueSegment> FQRoots { get; }
        public ImmutableArray<FinalizerQueueSegment> FQObjects { get; }
        public Dictionary<ulong, ulong> AllocationContext { get; }

        public ImmutableArray<ClrSegment> Segments { get; }

        public int LastSegmentIndex { get; set; }

        public VolatileHeapData(ClrHeap parent, IHeapHelpers _helpers)
        {
            _helpers.CreateSegments(parent,
                                    out ImmutableArray<ClrSegment> segments,
                                    out ImmutableArray<AllocationContext> allocContext,
                                    out ImmutableArray<FinalizerQueueSegment> fqRoots,
                                    out ImmutableArray<FinalizerQueueSegment> fqObjects);

            // Segments must be in sorted order.  We won't check all of them but we will at least check the beginning and end
            if (segments.Length > 0 && segments[0].Start > segments[segments.Length - 1].Start)
                Segments = segments.Sort((x, y) => x.Start.CompareTo(y.Start));
            else
                Segments = segments;

            FQRoots = fqRoots;
            FQObjects = fqObjects;
            AllocationContext = allocContext.ToDictionary(k => k.Pointer, v => v.Limit);
        }

        public ImmutableArray<(ulong Source, ulong Target)> GetDependentHandles(IHeapHelpers helpers)
        {
            if (!_dependentHandles.IsDefault)
                return _dependentHandles;

            var dependentHandles = helpers.EnumerateDependentHandleLinks().OrderBy(x => x.Source).ToImmutableArray();
            _dependentHandles = dependentHandles;
            return dependentHandles;
        }
    }
}