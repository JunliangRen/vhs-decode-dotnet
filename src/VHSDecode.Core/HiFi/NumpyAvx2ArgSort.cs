using System.Numerics;

namespace VHSDecode.Core.HiFi;

// Modified x86-simd-sort and Microsoft STL adaptations; see THIRD-PARTY-NOTICES.md.
internal static class NumpyAvx2ArgSort
{
    private const int LaneCount = 4;
    private const int PartitionUnroll = 4;
    private const int NetworkCapacity = 256;

    internal static int[] SortIndices(ReadOnlySpan<double> values)
    {
        var source = values.ToArray();
        var indices = Enumerable.Range(0, source.Length).ToArray();
        if (source.Length < 2 || IsSorted(source))
        {
            return indices;
        }

        if (source.Any(double.IsNaN))
        {
            Array.Sort(indices, (left, right) => CompareWithNaNs(source[left], source[right]));
            return indices;
        }

        int maxIterations = 2 * BitOperations.Log2((uint)source.Length);
        Sort(source, indices, 0, indices.Length - 1, maxIterations);
        return indices;
    }

    private static void Sort(
        double[] values,
        int[] indices,
        int left,
        int right,
        int maxIterations)
    {
        if (left >= right)
        {
            return;
        }

        if (maxIterations <= 0)
        {
            MsvcSort(values, indices, left, right + 1);
            return;
        }

        int length = right - left + 1;
        if (length <= NetworkCapacity)
        {
            SortNetwork(values, indices, left, length);
            return;
        }

        double pivot = SelectPivot(values, indices, left, right);
        int pivotIndex = Partition(
            values,
            indices,
            left,
            right + 1,
            pivot,
            out double smallest,
            out double biggest);
        if (pivot != smallest)
        {
            Sort(values, indices, left, pivotIndex - 1, maxIterations - 1);
        }

        if (pivot != biggest)
        {
            Sort(values, indices, pivotIndex, right, maxIterations - 1);
        }
    }

    // NumPy's Windows AVX2 argsort falls back to MSVC STL sorting after
    // exhausting its SIMD partition depth, including its equal-key swap order.
    private static void MsvcSort(
        double[] values,
        int[] indices,
        int first,
        int last)
        => MsvcSortUnchecked(values, indices, first, last, last - first);

    private static void MsvcSortUnchecked(
        double[] values,
        int[] indices,
        int first,
        int last,
        int ideal)
    {
        while (true)
        {
            if (last - first <= 32)
            {
                MsvcInsertionSort(values, indices, first, last);
                return;
            }

            if (ideal <= 0)
            {
                MsvcMakeHeap(values, indices, first, last);
                MsvcSortHeap(values, indices, first, last);
                return;
            }

            (int lowerEnd, int upperStart) = MsvcPartitionByMedianGuess(
                values,
                indices,
                first,
                last);
            ideal = (ideal >> 1) + (ideal >> 2);

            if (lowerEnd - first < last - upperStart)
            {
                MsvcSortUnchecked(values, indices, first, lowerEnd, ideal);
                first = upperStart;
            }
            else
            {
                MsvcSortUnchecked(values, indices, upperStart, last, ideal);
                last = lowerEnd;
            }
        }
    }

    private static void MsvcInsertionSort(
        double[] values,
        int[] indices,
        int first,
        int last)
    {
        if (first == last)
        {
            return;
        }

        for (int middle = first + 1; middle != last; middle++)
        {
            int hole = middle;
            int value = indices[middle];
            if (Less(values, value, indices[first]))
            {
                for (int position = middle; position > first; position--)
                {
                    indices[position] = indices[position - 1];
                }

                indices[first] = value;
            }
            else
            {
                while (Less(values, value, indices[hole - 1]))
                {
                    indices[hole] = indices[hole - 1];
                    hole--;
                }

                indices[hole] = value;
            }
        }
    }

    private static (int LowerEnd, int UpperStart) MsvcPartitionByMedianGuess(
        double[] values,
        int[] indices,
        int first,
        int last)
    {
        int middle = first + ((last - first) >> 1);
        MsvcGuessMedian(values, indices, first, middle, last - 1);
        int pivotFirst = middle;
        int pivotLast = pivotFirst + 1;

        while (first < pivotFirst &&
               !Less(values, indices[pivotFirst - 1], indices[pivotFirst]) &&
               !Less(values, indices[pivotFirst], indices[pivotFirst - 1]))
        {
            pivotFirst--;
        }

        while (pivotLast < last &&
               !Less(values, indices[pivotLast], indices[pivotFirst]) &&
               !Less(values, indices[pivotFirst], indices[pivotLast]))
        {
            pivotLast++;
        }

        int greaterFirst = pivotLast;
        int greaterLast = pivotFirst;
        while (true)
        {
            for (; greaterFirst < last; greaterFirst++)
            {
                if (Less(values, indices[pivotFirst], indices[greaterFirst]))
                {
                    continue;
                }

                if (Less(values, indices[greaterFirst], indices[pivotFirst]))
                {
                    break;
                }

                if (pivotLast != greaterFirst)
                {
                    Swap(indices, pivotLast, greaterFirst);
                    pivotLast++;
                }
                else
                {
                    pivotLast++;
                }
            }

            for (; first < greaterLast; greaterLast--)
            {
                int previous = greaterLast - 1;
                if (Less(values, indices[previous], indices[pivotFirst]))
                {
                    continue;
                }

                if (Less(values, indices[pivotFirst], indices[previous]))
                {
                    break;
                }

                pivotFirst--;
                if (pivotFirst != previous)
                {
                    Swap(indices, pivotFirst, previous);
                }
            }

            if (greaterLast == first && greaterFirst == last)
            {
                return (pivotFirst, pivotLast);
            }

            if (greaterLast == first)
            {
                if (pivotLast != greaterFirst)
                {
                    Swap(indices, pivotFirst, pivotLast);
                }

                pivotLast++;
                Swap(indices, pivotFirst, greaterFirst);
                pivotFirst++;
                greaterFirst++;
            }
            else if (greaterFirst == last)
            {
                greaterLast--;
                pivotFirst--;
                if (greaterLast != pivotFirst)
                {
                    Swap(indices, greaterLast, pivotFirst);
                }

                pivotLast--;
                Swap(indices, pivotFirst, pivotLast);
            }
            else
            {
                greaterLast--;
                Swap(indices, greaterFirst, greaterLast);
                greaterFirst++;
            }
        }
    }

    private static void MsvcGuessMedian(
        double[] values,
        int[] indices,
        int first,
        int middle,
        int last)
    {
        int count = last - first;
        if (count > 40)
        {
            int step = (count + 1) >> 3;
            int twoSteps = step << 1;
            MsvcMedianOfThree(values, indices, first, first + step, first + twoSteps);
            MsvcMedianOfThree(values, indices, middle - step, middle, middle + step);
            MsvcMedianOfThree(values, indices, last - twoSteps, last - step, last);
            MsvcMedianOfThree(values, indices, first + step, middle, last - step);
        }
        else
        {
            MsvcMedianOfThree(values, indices, first, middle, last);
        }
    }

    private static void MsvcMedianOfThree(
        double[] values,
        int[] indices,
        int first,
        int middle,
        int last)
    {
        if (Less(values, indices[middle], indices[first]))
        {
            Swap(indices, middle, first);
        }

        if (Less(values, indices[last], indices[middle]))
        {
            Swap(indices, last, middle);
            if (Less(values, indices[middle], indices[first]))
            {
                Swap(indices, middle, first);
            }
        }
    }

    private static void MsvcMakeHeap(
        double[] values,
        int[] indices,
        int first,
        int last)
    {
        int bottom = last - first;
        for (int hole = bottom >> 1; hole > 0;)
        {
            hole--;
            int value = indices[first + hole];
            MsvcPopHeapHoleByIndex(values, indices, first, hole, bottom, value);
        }
    }

    private static void MsvcSortHeap(
        double[] values,
        int[] indices,
        int first,
        int last)
    {
        while (last - first >= 2)
        {
            MsvcPopHeap(values, indices, first, last);
            last--;
        }
    }

    private static void MsvcPopHeap(
        double[] values,
        int[] indices,
        int first,
        int last)
    {
        if (last - first < 2)
        {
            return;
        }

        last--;
        int value = indices[last];
        indices[last] = indices[first];
        MsvcPopHeapHoleByIndex(values, indices, first, 0, last - first, value);
    }

    private static void MsvcPopHeapHoleByIndex(
        double[] values,
        int[] indices,
        int first,
        int hole,
        int bottom,
        int value)
    {
        int top = hole;
        int index = hole;
        int maximumNonLeaf = (bottom - 1) >> 1;
        while (index < maximumNonLeaf)
        {
            index = (2 * index) + 2;
            if (Less(values, indices[first + index], indices[first + index - 1]))
            {
                index--;
            }

            indices[first + hole] = indices[first + index];
            hole = index;
        }

        if (index == maximumNonLeaf && bottom % 2 == 0)
        {
            indices[first + hole] = indices[first + bottom - 1];
            hole = bottom - 1;
        }

        MsvcPushHeapByIndex(values, indices, first, hole, top, value);
    }

    private static void MsvcPushHeapByIndex(
        double[] values,
        int[] indices,
        int first,
        int hole,
        int top,
        int value)
    {
        int parent = (hole - 1) >> 1;
        while (top < hole && Less(values, indices[first + parent], value))
        {
            indices[first + hole] = indices[first + parent];
            hole = parent;
            parent = (hole - 1) >> 1;
        }

        indices[first + hole] = value;
    }

    private static bool Less(double[] values, int leftIndex, int rightIndex)
        => values[leftIndex] < values[rightIndex];

    private static void Swap(int[] values, int left, int right)
        => (values[left], values[right]) = (values[right], values[left]);

    private static int Partition(
        double[] values,
        int[] indices,
        int left,
        int right,
        double pivot,
        out double smallest,
        out double biggest)
    {
        smallest = double.PositiveInfinity;
        biggest = double.NegativeInfinity;
        int blockLength = PartitionUnroll * LaneCount;
        for (int remainder = (right - left) % blockLength; remainder > 0; remainder--)
        {
            double value = values[indices[left]];
            smallest = Math.Min(smallest, value);
            biggest = Math.Max(biggest, value);
            if (!(value < pivot))
            {
                right--;
                (indices[left], indices[right]) = (indices[right], indices[left]);
            }
            else
            {
                left++;
            }
        }

        if (left == right)
        {
            return left;
        }

        int[][] heldLeft = LoadBlock(indices, left);
        int[][] heldRight = LoadBlock(indices, right - blockLength);
        int rightStore = right - LaneCount;
        int leftStore = left;
        left += blockLength;
        right -= blockLength;
        while (right != left)
        {
            int[][] current;
            if ((rightStore + LaneCount) - right < left - leftStore)
            {
                right -= blockLength;
                current = LoadBlock(indices, right);
            }
            else
            {
                current = LoadBlock(indices, left);
                left += blockLength;
            }

            PartitionBlock(
                values,
                indices,
                current,
                pivot,
                ref leftStore,
                ref rightStore,
                ref smallest,
                ref biggest);
        }

        PartitionBlock(
            values,
            indices,
            heldLeft,
            pivot,
            ref leftStore,
            ref rightStore,
            ref smallest,
            ref biggest);
        PartitionBlock(
            values,
            indices,
            heldRight,
            pivot,
            ref leftStore,
            ref rightStore,
            ref smallest,
            ref biggest);
        return leftStore;
    }

    private static void PartitionBlock(
        double[] values,
        int[] indices,
        int[][] block,
        double pivot,
        ref int leftStore,
        ref int rightStore,
        ref double smallest,
        ref double biggest)
    {
        foreach (int[] vector in block)
        {
            int greaterOrEqual = PartitionVector(
                values,
                indices,
                vector,
                pivot,
                leftStore,
                rightStore,
                ref smallest,
                ref biggest);
            leftStore += LaneCount - greaterOrEqual;
            rightStore -= greaterOrEqual;
        }
    }

    private static int PartitionVector(
        double[] values,
        int[] indices,
        int[] vector,
        double pivot,
        int leftStore,
        int rightStore,
        ref double smallest,
        ref double biggest)
    {
        Span<int> permuted = stackalloc int[LaneCount];
        int lessPosition = 0;
        int greaterOrEqualPosition = LaneCount - 1;
        int greaterOrEqualCount = 0;
        for (int lane = 0; lane < LaneCount; lane++)
        {
            double value = values[vector[lane]];
            smallest = Math.Min(smallest, value);
            biggest = Math.Max(biggest, value);
            if (value >= pivot)
            {
                permuted[greaterOrEqualPosition--] = vector[lane];
                greaterOrEqualCount++;
            }
            else
            {
                permuted[lessPosition++] = vector[lane];
            }
        }

        for (int lane = 0; lane < LaneCount; lane++)
        {
            indices[leftStore + lane] = permuted[lane];
        }

        for (int lane = 0; lane < LaneCount; lane++)
        {
            indices[rightStore + lane] = permuted[lane];
        }

        return greaterOrEqualCount;
    }

    private static int[][] LoadBlock(int[] indices, int start)
    {
        var block = new int[PartitionUnroll][];
        for (int vector = 0; vector < block.Length; vector++)
        {
            block[vector] = indices.AsSpan(start + (vector * LaneCount), LaneCount).ToArray();
        }

        return block;
    }

    private static double SelectPivot(double[] values, int[] indices, int left, int right)
    {
        int spacing = (right - left) / LaneCount;
        Span<double> samples = stackalloc double[LaneCount]
        {
            values[indices[left + spacing]],
            values[indices[left + (2 * spacing)]],
            values[indices[left + (3 * spacing)]],
            values[indices[left + (4 * spacing)]]
        };
        samples.Sort();
        return samples[2];
    }

    private static void SortNetwork(double[] values, int[] indices, int start, int length)
    {
        int vectorCount = NetworkCapacity / LaneCount;
        while (vectorCount > 1 && length * 2 <= vectorCount * LaneCount)
        {
            vectorCount /= 2;
        }

        var vectors = new Lane[vectorCount][];
        for (int vector = 0; vector < vectorCount; vector++)
        {
            vectors[vector] = new Lane[LaneCount];
            for (int lane = 0; lane < LaneCount; lane++)
            {
                int offset = (vector * LaneCount) + lane;
                vectors[vector][lane] = offset < length
                    ? new Lane(values[indices[start + offset]], indices[start + offset])
                    : new Lane(double.PositiveInfinity, -1);
            }

            SortVector(vectors[vector]);
        }

        for (int vectorsPerMerge = 2;
             vectorsPerMerge <= vectorCount;
             vectorsPerMerge *= 2)
        {
            for (int vector = 0; vector < vectorCount; vector += vectorsPerMerge)
            {
                BitonicMerge(vectors, vector, vectorsPerMerge);
            }
        }

        for (int offset = 0; offset < length; offset++)
        {
            indices[start + offset] = vectors[offset / LaneCount][offset % LaneCount].Index;
        }
    }

    private static void BitonicMerge(Lane[][] vectors, int start, int count)
    {
        if (count == 2)
        {
            Reverse(vectors[start + 1]);
            CompareExchange(vectors[start], vectors[start + 1]);
            Reverse(vectors[start + 1]);
        }
        else
        {
            for (int i = 0; i < count / 2; i++)
            {
                int upper = start + count - i - 1;
                Reverse(vectors[upper]);
                CompareExchange(vectors[start + i], vectors[upper]);
                Reverse(vectors[upper]);
            }
        }

        for (int groupSize = count / 2; groupSize >= 2; groupSize /= 2)
        {
            for (int group = 0; group < count; group += groupSize)
            {
                for (int i = 0; i < groupSize / 2; i++)
                {
                    CompareExchange(
                        vectors[start + group + i],
                        vectors[start + group + i + (groupSize / 2)]);
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            MergeVector(vectors[start + i]);
        }
    }

    private static void SortVector(Lane[] vector)
    {
        CompareMerge(vector, [1, 0, 3, 2], 0xA);
        CompareMerge(vector, [3, 2, 1, 0], 0xC);
        CompareMerge(vector, [1, 0, 3, 2], 0xA);
    }

    private static void MergeVector(Lane[] vector)
    {
        CompareMerge(vector, [2, 3, 0, 1], 0xC);
        CompareMerge(vector, [1, 0, 3, 2], 0xA);
    }

    private static void CompareMerge(Lane[] vector, int[] permutation, int maximumMask)
    {
        Lane[] original = vector.ToArray();
        for (int lane = 0; lane < LaneCount; lane++)
        {
            Lane left = original[lane];
            Lane right = original[permutation[lane]];
            if (left.Key == right.Key)
            {
                vector[lane] = left;
                continue;
            }

            bool chooseMaximum = ((maximumMask >> lane) & 1) != 0;
            bool leftIsSmaller = left.Key < right.Key;
            vector[lane] = chooseMaximum == leftIsSmaller ? right : left;
        }
    }

    private static void CompareExchange(Lane[] lower, Lane[] upper)
    {
        for (int lane = 0; lane < LaneCount; lane++)
        {
            if (lower[lane].Key <= upper[lane].Key)
            {
                continue;
            }

            (lower[lane], upper[lane]) = (upper[lane], lower[lane]);
        }
    }

    private static void Reverse(Lane[] vector)
        => Array.Reverse(vector);

    private static bool IsSorted(ReadOnlySpan<double> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareWithNaNs(double left, double right)
    {
        if (double.IsNaN(left))
        {
            return double.IsNaN(right) ? 0 : 1;
        }

        return double.IsNaN(right) ? -1 : left.CompareTo(right);
    }

    private readonly record struct Lane(double Key, int Index);
}
