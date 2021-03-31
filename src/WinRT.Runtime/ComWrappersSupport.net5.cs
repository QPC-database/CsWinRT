﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WinRT.Interop;
using static System.Runtime.InteropServices.ComWrappers;

namespace WinRT
{
    public static partial class ComWrappersSupport
    {
        internal static readonly ConditionalWeakTable<object, InspectableInfo> InspectableInfoTable = new ConditionalWeakTable<object, InspectableInfo>();
        internal static readonly ThreadLocal<Type> CreateRCWType = new ThreadLocal<Type>();

        private static ComWrappers _comWrappers;
        private static object _comWrappersLock = new object();
        private static ComWrappers ComWrappers
        {
            get
            {
                if (_comWrappers is null)
                {
                    lock (_comWrappersLock)
                    {
                        if (_comWrappers is null)
                        {
                            _comWrappers = new DefaultComWrappers();
                            ComWrappers.RegisterForTrackerSupport(_comWrappers);
                        }
                    }
                }
                return _comWrappers;
            }
            set
            {
                lock (_comWrappersLock)
                {
                    _comWrappers = value;
                    ComWrappers.RegisterForTrackerSupport(_comWrappers);
                }
            }
        }

        internal static unsafe InspectableInfo GetInspectableInfo(IntPtr pThis)
        {
            var _this = FindObject<object>(pThis);
            return InspectableInfoTable.GetValue(_this, o => PregenerateNativeTypeInformation(o).inspectableInfo);
        }

        public static T CreateRcwForComObject<T>(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return default;
            }

            // CreateRCWType is a thread local which is set here to communicate the statically known type
            // when we are called by the ComWrappers API to create the object.  We can't pass this through the
            // ComWrappers API surface, so we are achieving it via a thread local.  We unset it after in case
            // there is other calls to it via other means.
            CreateRCWType.Value = typeof(T);
            var rcw = ComWrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.TrackerObject);
            CreateRCWType.Value = null;
            // Because .NET will de-duplicate strings and WinRT doesn't,
            // our RCW factory returns a wrapper of our string instance.
            // This ensures that ComWrappers never sees the same managed object for two different
            // native pointers. We unwrap here to ensure that the user-experience is expected
            // and consumers get a string object for a Windows.Foundation.IReference<String>.
            // We need to do the same thing for System.Type because there can be multiple WUX.Interop.TypeName's
            // for a single System.Type.

            // Resurrect IWinRTObject's disposed IObjectReferences, if necessary
            if (rcw is IWinRTObject winrtObj)
            {
                winrtObj.Resurrect();
            }

            return rcw switch
            {
                ABI.System.Nullable<string> ns => (T)(object) ns.Value,
                ABI.System.Nullable<Type> nt => (T)(object) nt.Value,
                _ => (T) rcw
            };
        }

        public static bool TryUnwrapObject(object o, out IObjectReference objRef)
        {
            // The unwrapping here needs to be an exact type match in case the user
            // has implemented a WinRT interface or inherited from a WinRT class
            // in a .NET (non-projected) type.

            if (o is Delegate del)
            {
                return TryUnwrapObject(del.Target, out objRef);
            }

            if (o is IWinRTObject winrtObj && winrtObj.HasUnwrappableNativeObject)
            {
                objRef = winrtObj.NativeObject;
                return true;
            }

            objRef = null;
            return false;
        }

        public static void RegisterObjectForInterface(object obj, IntPtr thisPtr, CreateObjectFlags createObjectFlags) => 
            ComWrappers.GetOrRegisterObjectForComInstance(thisPtr, createObjectFlags, obj);

        public static void RegisterObjectForInterface(object obj, IntPtr thisPtr) => 
            TryRegisterObjectForInterface(obj, thisPtr);

        public static object TryRegisterObjectForInterface(object obj, IntPtr thisPtr)
        {
            var rcw = ComWrappers.GetOrRegisterObjectForComInstance(thisPtr, CreateObjectFlags.TrackerObject, obj);

            // Resurrect IWinRTObject's disposed IObjectReferences, if necessary
            var target = rcw is Delegate del ? del.Target : rcw;
            if (target is IWinRTObject winrtObj)
            {
                winrtObj.Resurrect();
            }
            return rcw;
        }

        public static IObjectReference CreateCCWForObject(object obj)
        {
            IntPtr ccw = ComWrappers.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.TrackerSupport);
            return ObjectReference<IUnknownVftbl>.Attach(ref ccw);
        }

        public static unsafe T FindObject<T>(IntPtr ptr)
            where T : class => ComInterfaceDispatch.GetInstance<T>((ComInterfaceDispatch*)ptr);

        private static T FindDelegate<T>(IntPtr thisPtr)
            where T : class, System.Delegate => FindObject<T>(thisPtr);

        public static IUnknownVftbl IUnknownVftbl => DefaultComWrappers.IUnknownVftbl;
        public static IntPtr IUnknownVftblPtr => DefaultComWrappers.IUnknownVftblPtr;

        public static IntPtr AllocateVtableMemory(Type vtableType, int size) => RuntimeHelpers.AllocateTypeAssociatedMemory(vtableType, size);

        /// <summary>
        /// Initialize the global <see cref="System.Runtime.InteropServices.ComWrappers"/> instance to use for WinRT.
        /// </summary>
        /// <param name="wrappers">The wrappers instance to use, or the default if null.</param>
        /// <remarks>
        /// A custom ComWrappers instance can be supplied to enable programs to fast-track some type resolution
        /// instead of using reflection when the full type closure is known.
        /// </remarks>
        public static void InitializeComWrappers(ComWrappers wrappers = null)
        {
            ComWrappers = wrappers ?? new DefaultComWrappers();
        }

        internal static Func<IInspectable, object> GetTypedRcwFactory(string runtimeClassName) => TypedObjectFactoryCache.GetOrAdd(runtimeClassName, className => CreateTypedRcwFactory(className));
    
        
        private static Func<IInspectable, object> CreateFactoryForImplementationType(string runtimeClassName, Type implementationType)
        {
            if (implementationType.IsInterface)
            {
                return obj => obj;
            }
            
            ParameterExpression[] parms = new[] { Expression.Parameter(typeof(IInspectable), "inspectable") };

            return Expression.Lambda<Func<IInspectable, object>>(
                Expression.New(implementationType.GetConstructor(BindingFlags.NonPublic | BindingFlags.CreateInstance | BindingFlags.Instance, null, new[] { typeof(IObjectReference) }, null),
                    Expression.Property(parms[0], nameof(WinRT.IInspectable.ObjRef))),
                parms).Compile();
        }
    }

    public class ComWrappersHelper
    {
        public unsafe static void Init(
            bool isAggregation,
            object thisInstance,
            IntPtr newInstance,
            IntPtr inner,
            out IObjectReference objRef)
        {
            objRef = ComWrappersSupport.GetObjectReferenceForInterface(isAggregation ? inner : newInstance);

            IntPtr referenceTracker;
            {
                // Determine if the instance supports IReferenceTracker (e.g. WinUI).
                // Acquiring this interface is useful for:
                //   1) Providing an indication of what value to pass during RCW creation.
                //   2) Informing the Reference Tracker runtime during non-aggregation
                //      scenarios about new references.
                //
                // If aggregation, query the inner since that will have the implementation
                // otherwise the new instance will be used. Since the inner was composed
                // it should answer immediately without going through the outer. Either way
                // the reference count will go to the new instance.
                Guid iid = typeof(IReferenceTrackerVftbl).GUID;
                int hr = Marshal.QueryInterface(objRef.ThisPtr, ref iid, out referenceTracker);
                if (hr != 0)
                {
                    referenceTracker = default;
                }
            }

            {
                // Determine flags needed for native object wrapper (i.e. RCW) creation.
                var createObjectFlags = CreateObjectFlags.None;
                IntPtr instanceToWrap = newInstance;

                // The instance supports IReferenceTracker.
                if (referenceTracker != default(IntPtr))
                {
                    createObjectFlags |= CreateObjectFlags.TrackerObject;
                }

                // Update flags if the native instance is being used in an aggregation scenario.
                if (isAggregation)
                {
                    // Indicate the scenario is aggregation
                    createObjectFlags |= (CreateObjectFlags)4;

                    // The instance supports IReferenceTracker.
                    if (referenceTracker != default(IntPtr))
                    {
                        // IReferenceTracker is not needed in aggregation scenarios.
                        // It is not needed because all QueryInterface() calls on an
                        // object are followed by an immediately release of the returned
                        // pointer - see below for details.
                        Marshal.Release(referenceTracker);

                        // .NET 5 limitation
                        //
                        // For aggregated scenarios involving IReferenceTracker
                        // the API handles object cleanup. In .NET 5 the API
                        // didn't expose an option to handle this so we pass the inner
                        // in order to handle its lifetime.
                        //
                        // The API doesn't handle inner lifetime in any other scenario
                        // in the .NET 5 timeframe.
                        instanceToWrap = inner;
                    }
                }

                // Create a native object wrapper (i.e. RCW).
                //
                // Note this function will call QueryInterface() on the supplied instance,
                // therefore it is important that the enclosing CCW forwards to its inner
                // if aggregation is involved. This is typically accomplished through an
                // implementation of ICustomQueryInterface.
                ComWrappersSupport.RegisterObjectForInterface(thisInstance, instanceToWrap, createObjectFlags);
            }

            // The following sets up the object reference to correctly handle AddRefs and releases
            // based on the scenario.
            if (isAggregation)
            {
                // Aggregation scenarios should avoid calling AddRef() on the
                // newInstance value. This is due to the semantics of COM Aggregation
                // and the fact that calling an AddRef() on the instance will increment
                // the CCW which in turn will ensure it cannot be cleaned up. Calling
                // AddRef() on the instance when passed to unmanaged code is correct
                // since unmanaged code is required to call Release() at some point.

                // A pointer to the inner that should be queried for
                // additional interfaces. Immediately after a QueryInterface()
                // a Release() should be called on the returned pointer but the
                // pointer can be retained and used.  This is determined by the
                // IsAggregated and PreventReleaseOnDispose properties on IObjectReference.
                objRef.IsAggregated = true;
                // In WinUI scenario don't release inner
                objRef.PreventReleaseOnDispose = referenceTracker != default(IntPtr);
            }
            else
            {
                if (referenceTracker != default(IntPtr))
                {
                    // WinUI scenario
                    // This instance should be used to tell the
                    // Reference Tracker runtime whenever an AddRef()/Release()
                    // is performed on newInstance.
                    objRef.ReferenceTrackerPtr = referenceTracker;

                    // This instance is already AddRefFromTrackerSource by the CLR,
                    // so it would also ReleaseFromTrackerSource on destruction.
                    objRef.PreventReleaseFromTrackerSourceOnDispose = true;

                    Marshal.Release(referenceTracker);
                }

                Marshal.Release(newInstance);
            }
        }

        public unsafe static void Init(IObjectReference objRef)
        {
            if (objRef.ReferenceTrackerPtr == IntPtr.Zero)
            {
                Guid iid = typeof(IReferenceTrackerVftbl).GUID;
                int hr = Marshal.QueryInterface(objRef.ThisPtr, ref iid, out var referenceTracker);
                if (hr == 0)
                {
                    // WinUI scenario
                    // This instance should be used to tell the
                    // Reference Tracker runtime whenever an AddRef()/Release()
                    // is performed on newInstance.
                    objRef.ReferenceTrackerPtr = referenceTracker;

                    // This instance is already AddRefFromTrackerSource by the CLR,
                    // so it would also ReleaseFromTrackerSource on destruction.
                    objRef.PreventReleaseFromTrackerSourceOnDispose = true;
                    Marshal.Release(referenceTracker);
                }
            }
        }
    }

    public class DefaultComWrappers : ComWrappers
    {
        private static ConditionalWeakTable<object, VtableEntriesCleanupScout> ComInterfaceEntryCleanupTable = new ConditionalWeakTable<object, VtableEntriesCleanupScout>();
        public static unsafe IUnknownVftbl IUnknownVftbl => Unsafe.AsRef<IUnknownVftbl>(IUnknownVftblPtr.ToPointer());

        internal static IntPtr IUnknownVftblPtr { get; }

        static unsafe DefaultComWrappers()
        {
            GetIUnknownImpl(out var qi, out var addRef, out var release);

            IUnknownVftblPtr = RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(IUnknownVftbl), sizeof(IUnknownVftbl));
            (*(IUnknownVftbl*)IUnknownVftblPtr) = new IUnknownVftbl
            {
                QueryInterface = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)qi,
                AddRef = (delegate* unmanaged[Stdcall]<IntPtr, uint>)addRef,
                Release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)release,
            };
        }

        protected override unsafe ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            if (IsRuntimeImplementedRCW(obj))
            {
                // If the object is a runtime-implemented RCW, let the runtime create a CCW.
                count = 0;
                return null;
            }

            var entries = ComWrappersSupport.GetInterfaceTableEntries(obj);

            if (flags.HasFlag(CreateComInterfaceFlags.CallerDefinedIUnknown))
            {
                entries.Add(new ComInterfaceEntry
                {
                    IID = typeof(IUnknownVftbl).GUID,
                    Vtable = IUnknownVftbl.AbiToProjectionVftblPtr
                });
            }

            entries.Add(new ComInterfaceEntry
            {
                IID = typeof(IInspectable).GUID,
                Vtable = IInspectable.Vftbl.AbiToProjectionVftablePtr
            });

            count = entries.Count;
            ComInterfaceEntry* nativeEntries = (ComInterfaceEntry*)Marshal.AllocCoTaskMem(sizeof(ComInterfaceEntry) * count);

            for (int i = 0; i < count; i++)
            {
                nativeEntries[i] = entries[i];
            }

            ComInterfaceEntryCleanupTable.Add(obj, new VtableEntriesCleanupScout(nativeEntries));

            return nativeEntries;
        }

        private static unsafe bool IsRuntimeImplementedRCW(object obj)
        {
            Type t = obj.GetType();
            bool isRcw = t.IsCOMObject;
            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    if (arg.IsCOMObject)
                    {
                        isRcw = true;
                        break;
                    }
                }
            }
            return isRcw;
        }

        protected override object CreateObject(IntPtr externalComObject, CreateObjectFlags flags)
        {
            IObjectReference objRef = ComWrappersSupport.GetObjectReferenceForInterface(externalComObject);

            if (objRef.TryAs<IInspectable.Vftbl>(out var inspectableRef) == 0)
            {
                IInspectable inspectable = new IInspectable(inspectableRef);

                string runtimeClassName = ComWrappersSupport.GetRuntimeClassForTypeCreation(inspectable, ComWrappersSupport.CreateRCWType.Value);
                if (string.IsNullOrEmpty(runtimeClassName))
                {
                    // If the external IInspectable has not implemented GetRuntimeClassName,
                    // we use the Inspectable wrapper directly.
                    return inspectable;
                }
                return ComWrappersSupport.GetTypedRcwFactory(runtimeClassName)(inspectable);
            }
            else if (objRef.TryAs<ABI.WinRT.Interop.IWeakReference.Vftbl>(out var weakRef) == 0)
            {
                // IWeakReference is IUnknown-based, so implementations of it may not (and likely won't) implement
                // IInspectable. As a result, we need to check for them explicitly.
                
                return new SingleInterfaceOptimizedObject(typeof(IWeakReference), weakRef);
            }
            // If the external COM object isn't IInspectable or IWeakReference, we can't handle it.
            // If we're registered globally, we want to let the runtime fall back for IUnknown and IDispatch support.
            // Return null so the runtime can fall back gracefully in IUnknown and IDispatch scenarios.
            return null;
        }

        protected override void ReleaseObjects(IEnumerable objects)
        {
            foreach (var obj in objects)
            {
                if (ComWrappersSupport.TryUnwrapObject(obj, out var objRef))
                {
                    objRef.Dispose();
                }
                else
                {
                    throw new InvalidOperationException("Cannot release objects that are not runtime wrappers of native WinRT objects.");
                }
            }
        }

        unsafe class VtableEntriesCleanupScout
        {
            private readonly ComInterfaceEntry* _data;

            public VtableEntriesCleanupScout(ComInterfaceEntry* data)
            {
                _data = data;
            }

            ~VtableEntriesCleanupScout()
            {
                Marshal.FreeCoTaskMem((IntPtr)_data);
            }
        }
    }
}
