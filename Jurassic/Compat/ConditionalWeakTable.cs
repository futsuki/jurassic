
// File provided for Reference Use Only by Microsoft Corporation (c) 2007.
// ==++== 
//
//   Copyright (c) Microsoft Corporation.  All rights reserved.
//
// ==--== 
/*============================================================
** Class:  ConditionalWeakTable 
** 
** <owner>[....]</owner>
** 
** Description: Compiler support for runtime-generated "object fields."
**
**    Lets DLR and other language compilers expose the ability to
**    attach arbitrary "properties" to instanced managed objects at runtime. 
**
**    We expose this support as a dictionary whose keys are the 
**    instanced objects and the values are the "properties." 
**
**    Unlike a regular dictionary, ConditionalWeakTables will not 
**    keep keys alive.
**
**
** Lifetimes of keys and values: 
**
**    Inserting a key and value into the dictonary will not 
**    prevent the key from dying, even if the key is strongly reachable 
**    from the value.
** 
**    Prior to ConditionalWeakTable, the CLR did not expose
**    the functionality needed to implement this guarantee.
**
**    Once the key dies, the dictionary automatically removes 
**    the key/value entry.
** 
** 
** Relationship between ConditionalWeakTable and Dictionary:
** 
**    ConditionalWeakTable mirrors the form and functionality
**    of the IDictionary interface for the sake of api consistency.
**
**    Unlike Dictionary, ConditionalWeakTable is fully thread-safe 
**    and requires no additional locking to be done by callers.
** 
**    ConditionalWeakTable defines equality as Object.ReferenceEquals(). 
**    ConditionalWeakTable does not invoke GetHashCode() overrides.
** 
**    It is not intended to be a general purpose collection
**    and it does not formally implement IDictionary or
**    expose the full public surface area.
** 
**
** 
** Thread safety guarantees: 
**
**    ConditionalWeakTable is fully thread-safe and requires no 
**    additional locking to be done by callers.
**
**
** OOM guarantees: 
**
**    Will not corrupt unmanaged handle table on OOM. No guarantees 
**    about managed weak table consistency. Native handles reclamation 
**    may be delayed until appdomain shutdown.
===========================================================*/

namespace System.Runtime.CompilerServices
{
    using System;
    using System.Runtime.Versioning;
    using System.Runtime.InteropServices;


    #region ConditionalWeakTable 
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class ConditionalWeakTable<TKey, TValue>
        where TKey : class
        where TValue : class
    {

        #region Constructors 
        [System.Security.SecuritySafeCritical]
        public ConditionalWeakTable()
        {
            _buckets = new int[0];
            _entries = new Entry[0];
            _freeList = -1;
            _lock = new Object();

            Resize();   // Resize at once (so won't need "if initialized" checks all over) 
        }
        #endregion

        #region Public Members
        //-------------------------------------------------------------------------------------------
        // key:   key of the value to find. Cannot be null. 
        // value: if the key is found, contains the value associated with the key upon method return.
        //        if the key is not found, contains default(TValue). 
        // 
        // Method returns "true" if key was found, "false" otherwise.
        // 
        // Note: The key may get garbaged collected during the TryGetValue operation. If so, TryGetValue
        // may at its discretion, return "false" and set "value" to the default (as if the key was not present.)
        //-------------------------------------------------------------------------------------------
        [System.Security.SecuritySafeCritical]
        public bool TryGetValue(TKey key, out TValue value)
        {
            if (key == null)
            {
                throw new System.ArgumentNullException();
                //ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }
            lock (_lock)
            {
                VerifyIntegrity();
                return TryGetValueWorker(key, out value);
            }
        }

        //------------------------------------------------------------------------------------------- 
        // key: key to add. May not be null.
        // value: value to associate with key.
        //
        // If the key is already entered into the dictionary, this method throws an exception. 
        //
        // Note: The key may get garbage collected during the Add() operation. If so, Add() 
        // has the right to consider any prior entries successfully removed and add a new entry without 
        // throwing an exception.
        //-------------------------------------------------------------------------------------------- 
        [System.Security.SecuritySafeCritical]
        public void Add(TKey key, TValue value)
        {
            if (key == null)
            {
                throw new System.ArgumentNullException();
                //ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            lock (_lock)
            {
                VerifyIntegrity();
                _invalid = true;

                int entryIndex = FindEntry(key);
                if (entryIndex != -1)
                {
                    _invalid = false;
                    throw new System.ArgumentException();
                    //ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_AddingDuplicate);
                }

                CreateEntry(key, value);
                _invalid = false;
            }

        }

        //------------------------------------------------------------------------------------------- 
        // key: key to remove. May not be null.
        //
        // Returns true if the key is found and removed. Returns false if the key was not in the dictionary.
        // 
        // Note: The key may get garbage collected during the Remove() operation. If so,
        // Remove() will not fail or throw, however, the return value can be either true or false 
        // depending on who wins the ----. 
        //--------------------------------------------------------------------------------------------
        [System.Security.SecuritySafeCritical]
        public bool Remove(TKey key)
        {
            if (key == null)
            {
                throw new System.ArgumentNullException();
                //ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            lock (_lock)
            {
                VerifyIntegrity();
                _invalid = true;

                int hashCode = RuntimeHelpers.GetHashCode(key) & Int32.MaxValue;
                int bucket = hashCode % _buckets.Length;
                int last = -1;
                for (int entriesIndex = _buckets[bucket]; entriesIndex != -1; entriesIndex = _entries[entriesIndex].next)
                {
                    if (_entries[entriesIndex].hashCode == hashCode && _entries[entriesIndex].depHnd.GetPrimary() == key)
                    {
                        if (last == -1)
                        {
                            _buckets[bucket] = _entries[entriesIndex].next;
                        }
                        else
                        {
                            _entries[last].next = _entries[entriesIndex].next;
                        }

                        _entries[entriesIndex].depHnd.Free();
                        _entries[entriesIndex].next = _freeList;

                        _freeList = entriesIndex;

                        _invalid = false;
                        return true;

                    }
                    last = entriesIndex;
                }
                _invalid = false;
                return false;
            }
        }


        //--------------------------------------------------------------------------------------------
        // key:                 key of the value to find. Cannot be null.
        // createValueCallback: callback that creates value for key. Cannot be null.
        // 
        // Atomically tests if key exists in table. If so, returns corresponding value. If not,
        // invokes createValueCallback() passing it the key. The returned value is bound to the key in the table 
        // and returned as the result of GetValue(). 
        //
        // If multiple threads ---- to initialize the same key, the table may invoke createValueCallback 
        // multiple times with the same key. Exactly one of these calls will "win the ----" and the returned
        // value of that call will be the one added to the table and returned by all the racing GetValue() calls.
        //
        // This rule permits the table to invoke createValueCallback outside the internal table lock 
        // to prevent deadlocks.
        //------------------------------------------------------------------------------------------- 
        [System.Security.SecuritySafeCritical]
        public TValue GetValue(TKey key, CreateValueCallback createValueCallback)
        {
            // Our call to TryGetValue() validates key so no need for us to.
            //
            //  if (key == null)
            //  { 
            //      ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            //  } 

            if (createValueCallback == null)
            {
                throw new ArgumentNullException("createValueCallback");
            }

            TValue existingValue;
            if (TryGetValue(key, out existingValue))
            {
                return existingValue;
            }

            // If we got here, the key is not currently in table. Invoke the callback (outside the lock)
            // to generate the new value for the key.
            TValue newValue = createValueCallback(key);

            lock (_lock)
            {
                VerifyIntegrity();
                _invalid = true;

                // Now that we've retaken the lock, must recheck in case we lost a ---- to add the key.
                if (TryGetValueWorker(key, out existingValue))
                {
                    _invalid = false;
                    return existingValue;
                }
                else
                {
                    // Verified in-lock that we won the ---- to add the key. Add it now. 
                    CreateEntry(key, newValue);
                    _invalid = false;
                    return newValue;
                }
            }
        }

        //--------------------------------------------------------------------------------------------
        // key:                 key of the value to find. Cannot be null. 
        //
        // Helper method to call GetValue without passing a creation delegate.  Uses Activator.CreateInstance
        // to create new instances as needed.  If TValue does not have a default constructor, this will
        // throw. 
        //-------------------------------------------------------------------------------------------
        public TValue GetOrCreateValue(TKey key)
        {
            return GetValue(key, k => Activator.CreateInstance<TValue>());
        }

        public delegate TValue CreateValueCallback(TKey key);
        #endregion

        #region Private Members
        [System.Security.SecurityCritical]
        //--------------------------------------------------------------------------------------- 
        // Worker for finding a key/value pair
        // 
        // Preconditions:
        //     Must hold _lock.
        //     Key already validated as non-null
        //--------------------------------------------------------------------------------------- 
        private bool TryGetValueWorker(TKey key, out TValue value)
        {
            int entryIndex = FindEntry(key);
            if (entryIndex != -1)
            {
                Object primary = null;
                Object secondary = null;
                _entries[entryIndex].depHnd.GetPrimaryAndSecondary(out primary, out secondary);
                // Now that we've secured a strong reference to the secondary, must check the primary again 
                // to ensure it didn't expire (otherwise, we open a ---- where TryGetValue misreports an
                // expired key as a live key with a null value.) 
                if (primary != null)
                {
                    value = (TValue)secondary;
                    return true;
                }
            }

            value = default(TValue);
            return false;
        }

        //---------------------------------------------------------------------------------------- 
        // Worker for adding a new key/value pair.
        //
        // Preconditions:
        //     Must hold _lock. 
        //     Key already validated as non-null and not already in table.
        //--------------------------------------------------------------------------------------- 
        [System.Security.SecurityCritical]
        private void CreateEntry(TKey key, TValue value)
        {
            if (_freeList == -1)
            {
                Resize();
            }

            int hashCode = RuntimeHelpers.GetHashCode(key) & Int32.MaxValue;
            int bucket = hashCode % _buckets.Length;

            int newEntry = _freeList;
            _freeList = _entries[newEntry].next;

            _entries[newEntry].hashCode = hashCode;
            _entries[newEntry].depHnd = new DependentHandle(key, value);
            _entries[newEntry].next = _buckets[bucket];

            _buckets[bucket] = newEntry;

        }

        //----------------------------------------------------------------------------------------
        // This does two things: resize and scrub expired keys off bucket lists.
        // 
        // Precondition:
        //      Must hold _lock. 
        // 
        // Postcondition:
        //      _freeList is non-empty on exit. 
        //----------------------------------------------------------------------------------------
        public static class PrimeToolHash
        {
            static int[] primes;

            static PrimeToolHash()
            {
                //
                // Initialize array of first primes before methods are called.
                //
                primes = new int[]
                {
        3, 7, 11, 17, 23, 29, 37,
        47, 59, 71, 89, 107, 131,
        163, 197, 239, 293, 353,
        431, 521, 631, 761, 919,
        1103, 1327, 1597, 1931,
        2333, 2801, 3371, 4049,
        4861, 5839, 7013, 8419,
        10103, 12143, 14591, 17519,
        21023, 25229, 30293, 36353,
        43627, 52361, 62851, 75431,
        90523, 108631, 130363,
        156437, 187751, 225307,
        270371, 324449, 389357,
        467237, 560689, 672827,
        807403, 968897, 1162687,
        1395263, 1674319, 2009191,
        2411033, 2893249, 3471899,
        4166287, 4999559, 5999471,
        7199369
                };
            }

            public static int GetPrime(int min)
            {
                //
                // Get the first hashtable prime number
                // ... that is equal to or greater than the parameter.
                //
                for (int i = 0; i < primes.Length; i++)
                {
                    int num2 = primes[i];
                    if (num2 >= min)
                    {
                        return num2;
                    }
                }
                for (int j = min | 1; j < 2147483647; j += 2)
                {
                    if (PrimeTool.IsPrime(j))
                    {
                        return j;
                    }
                }
                return min;
            }
        }
        public static class PrimeTool
        {
            public static bool IsPrime(int candidate)
            {
                // Test whether the parameter is a prime number.
                if ((candidate & 1) == 0)
                {
                    if (candidate == 2)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                // Note:
                // ... This version was changed to test the square.
                // ... Original version tested against the square root.
                // ... Also we exclude 1 at the end.
                for (int i = 3; (i * i) <= candidate; i += 2)
                {
                    if ((candidate % i) == 0)
                    {
                        return false;
                    }
                }
                return candidate != 1;
            }
        }


        [System.Security.SecurityCritical]
        private void Resize()
        {
            // Start by assuming we won't resize.
            int newSize = _buckets.Length;

            // If any expired keys exist, we won't resize.
            bool hasExpiredEntries = false;
            int entriesIndex;
            for (entriesIndex = 0; entriesIndex < _entries.Length; entriesIndex++)
            {
                if (_entries[entriesIndex].depHnd.IsAllocated && _entries[entriesIndex].depHnd.GetPrimary() == null)
                {
                    hasExpiredEntries = true;
                    break;
                }
            }

            if (!hasExpiredEntries)
            {
                newSize = PrimeToolHash.GetPrime(_buckets.Length == 0 ? _initialCapacity + 1 : _buckets.Length * 2);
            }


            // Reallocate both buckets and entries and rebuild the bucket and freelists from scratch.
            // This serves both to scrub entries with expired keys and to put the new entries in the proper bucket. 
            int newFreeList = -1;
            int[] newBuckets = new int[newSize];
            for (int bucketIndex = 0; bucketIndex < newSize; bucketIndex++)
            {
                newBuckets[bucketIndex] = -1;
            }
            Entry[] newEntries = new Entry[newSize];

            // Migrate existing entries to the new table. 
            for (entriesIndex = 0; entriesIndex < _entries.Length; entriesIndex++)
            {
                DependentHandle depHnd = _entries[entriesIndex].depHnd;
                if (depHnd.IsAllocated && depHnd.GetPrimary() != null)
                {
                    // Entry is used and has not expired. Link it into the appropriate bucket list. 
                    int bucket = _entries[entriesIndex].hashCode % newSize;
                    newEntries[entriesIndex].depHnd = depHnd;
                    newEntries[entriesIndex].hashCode = _entries[entriesIndex].hashCode;
                    newEntries[entriesIndex].next = newBuckets[bucket];
                    newBuckets[bucket] = entriesIndex;
                }
                else
                {
                    // Entry has either expired or was on the freelist to begin with. Either way 
                    // insert it on the new freelist. 
                    _entries[entriesIndex].depHnd.Free();
                    newEntries[entriesIndex].depHnd = new DependentHandle();
                    newEntries[entriesIndex].next = newFreeList;
                    newFreeList = entriesIndex;
                }
            }

            // Add remaining entries to freelist. 
            while (entriesIndex != newEntries.Length)
            {
                newEntries[entriesIndex].depHnd = new DependentHandle();
                newEntries[entriesIndex].next = newFreeList;
                newFreeList = entriesIndex;
                entriesIndex++;
            }

            _buckets = newBuckets;
            _entries = newEntries;
            _freeList = newFreeList;
        }

        //---------------------------------------------------------------------------------------
        // Returns -1 if not found (if key expires during FindEntry, this can be treated as "not found.")
        // 
        // Preconditions:
        //     Must hold _lock. 
        //     Key already validated as non-null. 
        //----------------------------------------------------------------------------------------
        [System.Security.SecurityCritical]
        private int FindEntry(TKey key)
        {
            int hashCode = RuntimeHelpers.GetHashCode(key) & Int32.MaxValue;
            for (int entriesIndex = _buckets[hashCode % _buckets.Length]; entriesIndex != -1; entriesIndex = _entries[entriesIndex].next)
            {
                if (_entries[entriesIndex].hashCode == hashCode && _entries[entriesIndex].depHnd.GetPrimary() == key)
                {
                    return entriesIndex;
                }
            }
            return -1;
        }

        //---------------------------------------------------------------------------------------
        // Precondition: 
        //     Must hold _lock. 
        //---------------------------------------------------------------------------------------
        private void VerifyIntegrity()
        {
            if (_invalid)
            {
                //throw new InvalidOperationException(Environment.GetResourceString("CollectionCorrupted"));
                throw new InvalidOperationException("CollectionCorrupted");
            }
        }

        //---------------------------------------------------------------------------------------
        // Finalizer. 
        //----------------------------------------------------------------------------------------
        [System.Security.SecuritySafeCritical]
        ~ConditionalWeakTable()
        {

            // We're just freeing per-appdomain unmanaged handles here. If we're already shutting down the AD, 
            // don't bother. 
            //
            // (Despite its name, Environment.HasShutdownStart also returns true if the current AD is finalizing.) 
            if (Environment.HasShutdownStarted)
            {
                return;
            }

            if (_lock != null)
            {
                lock (_lock)
                {
                    if (_invalid)
                    {
                        return;
                    }
                    Entry[] entries = _entries;

                    // Make sure anyone sneaking into the table post-resurrection 
                    // gets booted before they can damage the native handle table.
                    _invalid = true;
                    _entries = null;
                    _buckets = null;

                    for (int entriesIndex = 0; entriesIndex < entries.Length; entriesIndex++)
                    {
                        entries[entriesIndex].depHnd.Free();
                    }
                }
            }
        }
        #endregion

        #region Private Data Members 
        //-------------------------------------------------------------------------------------------
        // Entry can be in one of three states: 
        // 
        //    - Linked into the freeList (_freeList points to first entry)
        //         depHnd.IsAllocated == false 
        //         hashCode == <dontcare>
        //         next links to next Entry on freelist)
        //
        //    - Used with live key (linked into a bucket list where _buckets[hashCode % _buckets.Length] points to first entry) 
        //         depHnd.IsAllocated == true, depHnd.GetPrimary() != null
        //         hashCode == RuntimeHelpers.GetHashCode(depHnd.GetPrimary()) & Int32.MaxValue 
        //         next links to next Entry in bucket. 
        //
        //    - Used with dead key (linked into a bucket list where _buckets[hashCode % _buckets.Length] points to first entry) 
        //         depHnd.IsAllocated == true, depHnd.GetPrimary() == null
        //         hashCode == <notcare>
        //         next links to next Entry in bucket.
        // 
        // The only difference between "used with live key" and "used with dead key" is that
        // depHnd.GetPrimary() returns null. The transition from "used with live key" to "used with dead key" 
        // happens asynchronously as a result of normal garbage collection. The dictionary itself 
        // receives no notification when this happens.
        // 
        // When the dictionary grows the _entries table, it scours it for expired keys and puts those
        // entries back on the freelist.
        //--------------------------------------------------------------------------------------------
        private struct Entry
        {
            public DependentHandle depHnd;      // Holds key and value using a weak reference for the key and a strong reference 
                                                // for the value that is traversed only if the key is reachable without going through the value. 
            public int hashCode;    // Cached copy of key's hashcode
            public int next;        // Index of next entry, -1 if last 
        }

        private int[] _buckets;             // _buckets[hashcode & _buckets.Length] contains index of first entry in bucket (-1 if empty)
        private Entry[] _entries;
        private int _freeList;            // -1 = empty, else index of first unused Entry
        private const int _initialCapacity = 5;
        private Object _lock;                // this could be a ReaderWriterLock but CoreCLR does not support RWLocks. 
        private bool _invalid;             // flag detects if OOM or other background exception threw us out of the lock.
        #endregion 
    }
    #endregion




    #region DependentHandle 
    //==========================================================================================
    // This struct collects all operations on native DependentHandles. The DependentHandle 
    // merely wraps an IntPtr so this struct serves mainly as a "managed typedef."
    //
    // DependentHandles exist in one of two states:
    // 
    //    IsAllocated == false
    //        No actual handle is allocated underneath. Illegal to call GetPrimary 
    //        or GetPrimaryAndSecondary(). Ok to call Free(). 
    //
    //        Initializing a DependentHandle using the nullary ctor creates a DependentHandle 
    //        that's in the !IsAllocated state.
    //        (! Right now, we get this guarantee for free because (IntPtr)0 == NULL unmanaged handle.
    //         ! If that assertion ever becomes false, we'll have to add an _isAllocated field
    //         ! to compensate.) 
    //
    // 
    //    IsAllocated == true 
    //        There's a handle allocated underneath. You must call Free() on this eventually
    //        or you cause a native handle table leak. 
    //
    // This struct intentionally does no self-synchronization. It's up to the caller to
    // to use DependentHandles in a thread-safe way.
    //========================================================================================= 
    [ComVisible(false)]
    struct DependentHandle
    {
        #region Constructors
        [System.Security.SecurityCritical]
        public DependentHandle(Object primary, Object secondary)
        {
            IntPtr handle = (IntPtr)0;
            nInitialize(primary, secondary, out handle);
            // no need to check for null result: nInitialize expected to throw OOM.
            _handle = handle;
        }
        #endregion

        #region Public Members
        public bool IsAllocated
        {
            get
            {
                return _handle != (IntPtr)0;
            }
        }

        // Getting the secondary object is more expensive than getting the first so
        // we provide a separate primary-only accessor for those times we only want the
        // primary.
        [System.Security.SecurityCritical]
        public Object GetPrimary()
        {
            Object primary;
            nGetPrimary(_handle, out primary);
            return primary;
        }

        [System.Security.SecurityCritical]
        public void GetPrimaryAndSecondary(out Object primary, out Object secondary)
        {
            nGetPrimaryAndSecondary(_handle, out primary, out secondary);
        }

        //---------------------------------------------------------------------- 
        // Forces dependentHandle back to non-allocated state (if not already there)
        // and frees the handle if needed.
        //---------------------------------------------------------------------
        [System.Security.SecurityCritical]
        public void Free()
        {
            if (_handle != (IntPtr)0)
            {
                IntPtr handle = _handle;
                _handle = (IntPtr)0;
                nFree(handle);
            }
        }
        #endregion

        #region Private Members 
        [System.Security.SecurityCritical]
        [ResourceExposure(ResourceScope.AppDomain)]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void nInitialize(Object primary, Object secondary, out IntPtr dependentHandle);

        [System.Security.SecurityCritical]
        [ResourceExposure(ResourceScope.None)]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void nGetPrimary(IntPtr dependentHandle, out Object primary);

        [System.Security.SecurityCritical]
        [ResourceExposure(ResourceScope.None)]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void nGetPrimaryAndSecondary(IntPtr dependentHandle, out Object primary, out Object secondary);

        [System.Security.SecurityCritical]
        [ResourceExposure(ResourceScope.None)]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void nFree(IntPtr dependentHandle);
        #endregion

        #region Private Data Member
        private IntPtr _handle;
        #endregion

    } // struct DependentHandle 
    #endregion 
}


// File provided for Reference Use Only by Microsoft Corporation (c) 2007.