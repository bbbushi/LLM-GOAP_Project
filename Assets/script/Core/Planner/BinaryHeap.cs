using System;
using System.Collections.Generic;

namespace Vibe.Core
{
    /// <summary>
    /// 数组二叉最小堆——GOAP.md §4 教学版简化点 1 的正式解法
    /// （open 集线性扫描 O(n) 取最小 → O(log n) 优先队列，Unity API 兼容级别下无现成实现故自写）。
    /// 用 <see cref="Comparison{T}"/> 委托定序；同键元素的出队次序不作保证，
    /// 需要稳定序时由调用方的比较器编码插入序号（<see cref="GoapPlanner"/> 即如此）。
    /// 仅限模拟内核内部使用（internal），不是对外契约。
    /// </summary>
    internal sealed class BinaryHeap<T>
    {
        private readonly Comparison<T> _compare;
        private readonly List<T> _items = new List<T>();

        public BinaryHeap(Comparison<T> comparison)
        {
            _compare = comparison ?? throw new ArgumentNullException(nameof(comparison));
        }

        /// <summary>堆内元素数。</summary>
        public int Count => _items.Count;

        /// <summary>入堆并上滤恢复堆序，O(log n)。</summary>
        public void Push(T item)
        {
            _items.Add(item);
            SiftUp(_items.Count - 1);
        }

        /// <summary>取出堆顶（最小）元素并下滤恢复堆序，O(log n)；空堆抛 <see cref="InvalidOperationException"/>。</summary>
        public T Pop()
        {
            T top = Peek();
            int last = _items.Count - 1;
            _items[0] = _items[last];
            _items.RemoveAt(last);
            if (_items.Count > 1) SiftDown(0);
            return top;
        }

        /// <summary>查看堆顶元素但不移除；空堆抛 <see cref="InvalidOperationException"/>。</summary>
        public T Peek()
        {
            if (_items.Count == 0) throw new InvalidOperationException("堆为空，无法 Peek/Pop");
            return _items[0];
        }

        private void SiftUp(int i)
        {
            T item = _items[i];
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_compare(_items[parent], item) <= 0) break;
                _items[i] = _items[parent];
                i = parent;
            }
            _items[i] = item;
        }

        private void SiftDown(int i)
        {
            T item = _items[i];
            int count = _items.Count;
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= count) break;
                int right = child + 1;
                if (right < count && _compare(_items[right], _items[child]) < 0) child = right;
                if (_compare(_items[child], item) >= 0) break;
                _items[i] = _items[child];
                i = child;
            }
            _items[i] = item;
        }
    }
}
