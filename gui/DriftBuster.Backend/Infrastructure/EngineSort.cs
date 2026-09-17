namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// CPython 3.13's <c>list.sort()</c> (no key, not reversed) over a caller-supplied <c>&lt;</c>: natural runs found and
/// made ascending (reversing strictly descending runs with their equal sub-runs kept in order), short runs extended
/// to the computed minimum run length by binary insertion, runs merged by the powersort policy, and merges that
/// gallop once one side wins <see cref="MinGallop"/> times in a row, with the adaptive gallop threshold. Every
/// comparison is made in the interpreter's order with the interpreter's operands, so a comparison that is not a
/// consistent order (a float NaN, which is neither less nor greater than anything) leaves the elements exactly
/// where CPython leaves them, and a comparison that throws does so at the same pair.
/// </summary>
/// <remarks>
/// A C# reimplementation of the algorithm CPython 3.13 documents in <c>Objects/listsort.txt</c> and implements in
/// <c>Objects/listobject.c</c> (PSF License), control flow kept step for step so the comparison sequence is the same;
/// checked against the interpreter's <c>sorted()</c> over lists holding NaN.
/// </remarks>
public sealed class EngineSort<T>
{
    private const int MaxMinrun = 64;
    private const int MinGallop = 7;

    private readonly T[] _a;
    private readonly Func<T, T, bool> _lessThan;
    private readonly List<(int Base, int Length, int Power)> _pending = [];
    private T[] _temp = [];
    private int _minGallop = MinGallop;

    private EngineSort(T[] items, Func<T, T, bool> lessThan)
    {
        _a = items;
        _lessThan = lessThan;
    }

    /// <summary>Sorts <paramref name="items"/> in place as <c>items.sort()</c> would with <paramref name="lessThan"/> as <c>&lt;</c>.</summary>
    public static void Sort(IList<T> items, Func<T, T, bool> lessThan)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(lessThan);
        var buffer = items.ToArray();
        new EngineSort<T>(buffer, lessThan).Run();
        for (var index = 0; index < buffer.Length; index++)
        {
            items[index] = buffer[index];
        }
    }

    private bool Lt(T left, T right) => _lessThan(left, right);

    private void Run()
    {
        var remaining = _a.Length;
        if (remaining < 2)
        {
            return;
        }

        var minrun = ComputeMinrun(remaining);
        var lo = 0;
        do
        {
            var n = CountRun(lo, remaining);
            if (n < minrun)
            {
                var force = Math.Min(remaining, minrun);
                BinarySort(lo, force, n);
                n = force;
            }

            FoundNewRun(n);
            _pending.Add((lo, n, 0));
            lo += n;
            remaining -= n;
        }
        while (remaining > 0);

        MergeForceCollapse();
    }

    private static int ComputeMinrun(int n)
    {
        var r = 0;
        while (n >= MaxMinrun)
        {
            r |= n & 1;
            n >>= 1;
        }

        return n + r;
    }

    // Stable binary insertion of a[lo + ok .. lo + n) into the sorted prefix a[lo .. lo + ok).
    private void BinarySort(int lo, int n, int ok)
    {
        if (ok == 0)
        {
            ok = 1;
        }

        for (; ok < n; ok++)
        {
            var pivot = _a[lo + ok];
            int left = 0, right = ok;
            do
            {
                var mid = (left + right) >> 1;
                if (Lt(pivot, _a[lo + mid]))
                {
                    right = mid;
                }
                else
                {
                    left = mid + 1;
                }
            }
            while (left < right);

            Array.Copy(_a, lo + left, _a, lo + left + 1, ok - left);
            _a[lo + left] = pivot;
        }
    }

    private void Reverse(int start, int count) => Array.Reverse(_a, start, count);

    // The length of the run at a[lo], made ascending in place.
    private int CountRun(int lo, int remaining)
    {
        int n;
        for (n = 1; n < remaining; n++)
        {
            if (Lt(_a[lo + n], _a[lo + n - 1]))
            {
                break;
            }
        }

        if (n == remaining)
        {
            return n;
        }

        // An ascending prefix longer than one that is not all equal ends the run; an all-equal one starts a descending run.
        if (n > 1)
        {
            if (Lt(_a[lo], _a[lo + n - 1]))
            {
                return n;
            }

            Reverse(lo, n);
        }

        n++;
        return CountDescendingRun(lo, n, remaining);
    }

    // Finishes a descending run whose first n elements are resolved, reversing all-equal sub-runs as they end so the
    // final whole-run reversal restores their order, then extends the ascending result by a naturally ascending suffix.
    private int CountDescendingRun(int lo, int n, int remaining)
    {
        var equal = 0;
        for (; n < remaining; n++)
        {
            if (Lt(_a[lo + n], _a[lo + n - 1]))
            {
                ReverseLastEqual(lo, n, ref equal);
            }
            else if (Lt(_a[lo + n - 1], _a[lo + n]))
            {
                break;
            }
            else
            {
                equal++;
            }
        }

        ReverseLastEqual(lo, n, ref equal);
        Reverse(lo, n);
        for (; n < remaining; n++)
        {
            if (Lt(_a[lo + n], _a[lo + n - 1]))
            {
                break;
            }
        }

        return n;
    }

    private void ReverseLastEqual(int lo, int n, ref int equal)
    {
        if (equal == 0)
        {
            return;
        }

        equal++;
        Reverse(lo + n - equal, equal);
        equal = 0;
    }

    // a[hint] of the n elements at array[start] is where the search begins; returns k with array[k-1] < key <= array[k].
    private int GallopLeft(T key, T[] array, int start, int n, int hint)
    {
        int lastOffset = 0, offset = 1;
        if (Lt(array[start + hint], key))
        {
            var maxOffset = n - hint;
            while (offset < maxOffset && Lt(array[start + hint + offset], key))
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
            }

            offset = Math.Min(offset, maxOffset);
            lastOffset += hint;
            offset += hint;
        }
        else
        {
            var maxOffset = hint + 1;
            while (offset < maxOffset && !Lt(array[start + hint - offset], key))
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
            }

            offset = Math.Min(offset, maxOffset);
            (lastOffset, offset) = (hint - offset, hint - lastOffset);
        }

        lastOffset++;
        while (lastOffset < offset)
        {
            var mid = lastOffset + ((offset - lastOffset) >> 1);
            if (Lt(array[start + mid], key))
            {
                lastOffset = mid + 1;
            }
            else
            {
                offset = mid;
            }
        }

        return offset;
    }

    // As GallopLeft, but past the rightmost equal element: array[k-1] <= key < array[k].
    private int GallopRight(T key, T[] array, int start, int n, int hint)
    {
        int lastOffset = 0, offset = 1;
        if (Lt(key, array[start + hint]))
        {
            var maxOffset = hint + 1;
            while (offset < maxOffset && Lt(key, array[start + hint - offset]))
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
            }

            offset = Math.Min(offset, maxOffset);
            (lastOffset, offset) = (hint - offset, hint - lastOffset);
        }
        else
        {
            var maxOffset = n - hint;
            while (offset < maxOffset && !Lt(key, array[start + hint + offset]))
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
            }

            offset = Math.Min(offset, maxOffset);
            lastOffset += hint;
            offset += hint;
        }

        lastOffset++;
        while (lastOffset < offset)
        {
            var mid = lastOffset + ((offset - lastOffset) >> 1);
            if (Lt(key, array[start + mid]))
            {
                offset = mid;
            }
            else
            {
                lastOffset = mid + 1;
            }
        }

        return offset;
    }

    private T[] Temp(int need)
    {
        if (_temp.Length < need)
        {
            _temp = new T[need];
        }

        return _temp;
    }

    // Merges the runs at stack indices i and i + 1.
    private void MergeAt(int i)
    {
        var (baseA, lengthA, powerA) = _pending[i];
        var (baseB, lengthB, _) = _pending[i + 1];
        _pending[i] = (baseA, lengthA + lengthB, powerA);
        _pending.RemoveAt(i + 1);

        var k = GallopRight(_a[baseB], _a, baseA, lengthA, 0);
        baseA += k;
        lengthA -= k;
        if (lengthA == 0)
        {
            return;
        }

        lengthB = GallopLeft(_a[baseA + lengthA - 1], _a, baseB, lengthB, lengthB - 1);
        if (lengthB <= 0)
        {
            return;
        }

        if (lengthA <= lengthB)
        {
            new LowMerge(this, baseA, lengthA, baseB, lengthB).Run();
        }
        else
        {
            new HighMerge(this, baseA, lengthA, baseB, lengthB).Run();
        }
    }

    // The depth of the boundary between the run at s1 (length n1) and the next (length n2) in the list of length n.
    private static int PowerLoop(int s1, int n1, int n2, int n)
    {
        var result = 0;
        long a = (2L * s1) + n1;
        var b = a + n1 + n2;
        while (true)
        {
            result++;
            if (a >= n)
            {
                a -= n;
                b -= n;
            }
            else if (b >= n)
            {
                break;
            }

            a <<= 1;
            b <<= 1;
        }

        return result;
    }

    private void FoundNewRun(int n2)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var (s1, n1, _) = _pending[^1];
        var power = PowerLoop(s1, n1, n2, _a.Length);
        while (_pending.Count > 1 && _pending[^2].Power > power)
        {
            MergeAt(_pending.Count - 2);
        }

        _pending[^1] = (_pending[^1].Base, _pending[^1].Length, power);
    }

    private void MergeForceCollapse()
    {
        while (_pending.Count > 1)
        {
            var n = _pending.Count - 2;
            if (n > 0 && _pending[n - 1].Length < _pending[n + 1].Length)
            {
                n--;
            }

            MergeAt(n);
        }
    }

    /// <summary>merge_lo: run A (the shorter) is copied out and merged left to right into place.</summary>
    private sealed class LowMerge
    {
        private readonly EngineSort<T> _sort;
        private readonly T[] _a;
        private readonly T[] _tmp;
        private int _dest;
        private int _pa;
        private int _pb;
        private int _na;
        private int _nb;

        public LowMerge(EngineSort<T> sort, int baseA, int lengthA, int baseB, int lengthB)
        {
            _sort = sort;
            _a = sort._a;
            _tmp = sort.Temp(lengthA);
            _dest = baseA;
            _pb = baseB;
            _na = lengthA;
            _nb = lengthB;
        }

        public void Run()
        {
            Array.Copy(_a, _dest, _tmp, 0, _na);
            _a[_dest++] = _a[_pb++];
            if (--_nb == 0)
            {
                Finish();
                return;
            }

            if (_na == 1)
            {
                CopyB();
                return;
            }

            var outcome = MergeLoop();
            if (outcome)
            {
                CopyB();
            }
            else
            {
                Finish();
            }
        }

        // True when run A is down to its last element (CopyB), false when run B is exhausted (Succeed).
        private bool MergeLoop()
        {
            var minGallop = _sort._minGallop;
            while (true)
            {
                var one = OneAtATime(minGallop);
                if (one is not null)
                {
                    return one.Value;
                }

                minGallop++;
                bool? galloped;
                do
                {
                    minGallop -= minGallop > 1 ? 1 : 0;
                    _sort._minGallop = minGallop;
                    galloped = GallopStep(out var aCount, out var bCount);
                    if (galloped is not null)
                    {
                        return galloped.Value;
                    }

                    if (aCount < MinGallop && bCount < MinGallop)
                    {
                        break;
                    }
                }
                while (true);

                minGallop++;
                _sort._minGallop = minGallop;
            }
        }

        // Compares element by element until one side wins minGallop times in a row (null) or the merge ends.
        private bool? OneAtATime(int minGallop)
        {
            int aCount = 0, bCount = 0;
            while (true)
            {
                if (_sort.Lt(_a[_pb], _tmp[_pa]))
                {
                    _a[_dest++] = _a[_pb++];
                    bCount++;
                    aCount = 0;
                    if (--_nb == 0)
                    {
                        return false;
                    }

                    if (bCount >= minGallop)
                    {
                        return null;
                    }
                }
                else
                {
                    _a[_dest++] = _tmp[_pa++];
                    aCount++;
                    bCount = 0;
                    if (--_na == 1)
                    {
                        return true;
                    }

                    if (aCount >= minGallop)
                    {
                        return null;
                    }
                }
            }
        }

        private bool? GallopStep(out int aCount, out int bCount)
        {
            bCount = 0;
            var k = _sort.GallopRight(_a[_pb], _tmp, _pa, _na, 0);
            aCount = k;
            if (k > 0)
            {
                Array.Copy(_tmp, _pa, _a, _dest, k);
                _dest += k;
                _pa += k;
                _na -= k;
                if (_na == 1)
                {
                    return true;
                }

                // Only an inconsistent comparison can empty run A here.
                if (_na == 0)
                {
                    return false;
                }
            }

            _a[_dest++] = _a[_pb++];
            if (--_nb == 0)
            {
                return false;
            }

            k = _sort.GallopLeft(_tmp[_pa], _a, _pb, _nb, 0);
            bCount = k;
            if (k > 0)
            {
                Array.Copy(_a, _pb, _a, _dest, k);
                _dest += k;
                _pb += k;
                _nb -= k;
                if (_nb == 0)
                {
                    return false;
                }
            }

            _a[_dest++] = _tmp[_pa++];
            return --_na == 1 ? true : null;
        }

        private void Finish()
        {
            if (_na > 0)
            {
                Array.Copy(_tmp, _pa, _a, _dest, _na);
            }
        }

        private void CopyB()
        {
            Array.Copy(_a, _pb, _a, _dest, _nb);
            _a[_dest + _nb] = _tmp[_pa];
        }
    }

    /// <summary>merge_hi: run B (the shorter) is copied out and merged right to left into place.</summary>
    private sealed class HighMerge
    {
        private readonly EngineSort<T> _sort;
        private readonly T[] _a;
        private readonly T[] _tmp;
        private readonly int _baseA;
        private int _dest;
        private int _pa;
        private int _pb;
        private int _na;
        private int _nb;

        public HighMerge(EngineSort<T> sort, int baseA, int lengthA, int baseB, int lengthB)
        {
            _sort = sort;
            _a = sort._a;
            _tmp = sort.Temp(lengthB);
            _baseA = baseA;
            _dest = baseB + lengthB - 1;
            _pa = baseA + lengthA - 1;
            _pb = lengthB - 1;
            _na = lengthA;
            _nb = lengthB;
        }

        public void Run()
        {
            Array.Copy(_a, _dest - (_nb - 1), _tmp, 0, _nb);
            _a[_dest--] = _a[_pa--];
            if (--_na == 0)
            {
                Finish();
                return;
            }

            if (_nb == 1)
            {
                CopyA();
                return;
            }

            if (MergeLoop())
            {
                CopyA();
            }
            else
            {
                Finish();
            }
        }

        // True when run B is down to its first element (CopyA), false when run A is exhausted (Succeed).
        private bool MergeLoop()
        {
            var minGallop = _sort._minGallop;
            while (true)
            {
                var one = OneAtATime(minGallop);
                if (one is not null)
                {
                    return one.Value;
                }

                minGallop++;
                while (true)
                {
                    minGallop -= minGallop > 1 ? 1 : 0;
                    _sort._minGallop = minGallop;
                    var galloped = GallopStep(out var aCount, out var bCount);
                    if (galloped is not null)
                    {
                        return galloped.Value;
                    }

                    if (aCount < MinGallop && bCount < MinGallop)
                    {
                        break;
                    }
                }

                minGallop++;
                _sort._minGallop = minGallop;
            }
        }

        private bool? OneAtATime(int minGallop)
        {
            int aCount = 0, bCount = 0;
            while (true)
            {
                if (_sort.Lt(_tmp[_pb], _a[_pa]))
                {
                    _a[_dest--] = _a[_pa--];
                    aCount++;
                    bCount = 0;
                    if (--_na == 0)
                    {
                        return false;
                    }

                    if (aCount >= minGallop)
                    {
                        return null;
                    }
                }
                else
                {
                    _a[_dest--] = _tmp[_pb--];
                    bCount++;
                    aCount = 0;
                    if (--_nb == 1)
                    {
                        return true;
                    }

                    if (bCount >= minGallop)
                    {
                        return null;
                    }
                }
            }
        }

        private bool? GallopStep(out int aCount, out int bCount)
        {
            bCount = 0;
            var k = _na - _sort.GallopRight(_tmp[_pb], _a, _baseA, _na, _na - 1);
            aCount = k;
            if (k > 0)
            {
                _dest -= k;
                _pa -= k;
                Array.Copy(_a, _pa + 1, _a, _dest + 1, k);
                _na -= k;
                if (_na == 0)
                {
                    return false;
                }
            }

            _a[_dest--] = _tmp[_pb--];
            if (--_nb == 1)
            {
                return true;
            }

            k = _nb - _sort.GallopLeft(_a[_pa], _tmp, 0, _nb, _nb - 1);
            bCount = k;
            if (k > 0)
            {
                _dest -= k;
                _pb -= k;
                Array.Copy(_tmp, _pb + 1, _a, _dest + 1, k);
                _nb -= k;
                if (_nb == 1)
                {
                    return true;
                }

                // Only an inconsistent comparison can empty run B here.
                if (_nb == 0)
                {
                    return false;
                }
            }

            _a[_dest--] = _a[_pa--];
            return --_na == 0 ? false : null;
        }

        private void Finish()
        {
            if (_nb > 0)
            {
                Array.Copy(_tmp, 0, _a, _dest - (_nb - 1), _nb);
            }
        }

        private void CopyA()
        {
            _dest -= _na;
            _pa -= _na;
            Array.Copy(_a, _pa + 1, _a, _dest + 1, _na);
            _a[_dest] = _tmp[_pb];
        }
    }
}
