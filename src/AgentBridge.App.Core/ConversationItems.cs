using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AgentBridge.App.Core;

// The conversation's blocks as the list's items (#765): an ObservableCollection's changes, one item at
// a time as the list takes them, kept in a ring rather than an array. A block taken off the front, as
// a response cut at its start to fit loses its earliest blocks, moves no other: in an
// ObservableCollection each RemoveAt(0) shifted every block after it, so 2,048 taken off 4,096 moved
// 6.3 million references on the UI thread. A change at either end moves nothing; one inside moves the
// blocks on its shorter side.
public sealed class ConversationItems : Collection<ConversationBlock>, INotifyCollectionChanged, INotifyPropertyChanged
{
    readonly Ring ring;

    public ConversationItems() : this(new Ring()) { }

    public ConversationItems(IEnumerable<ConversationBlock> blocks) : this()
    {
        foreach (var block in blocks) { ring.Add(block); }
    }

    ConversationItems(Ring ring) : base(ring) => this.ring = ring;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    // How many blocks the changes so far have moved to make room or close a gap.
    internal long Shifted => ring.Shifted;

    protected override void InsertItem(int index, ConversationBlock item)
    {
        base.InsertItem(index, item);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index), counted: true);
    }

    protected override void RemoveItem(int index)
    {
        var item = this[index];
        base.RemoveItem(index);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index), counted: true);
    }

    protected override void SetItem(int index, ConversationBlock item)
    {
        var old = this[index];
        base.SetItem(index, item);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, item, old, index), counted: false);
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset), counted: true);
    }

    void Changed(NotifyCollectionChangedEventArgs e, bool counted)
    {
        if (counted) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CollectionChanged?.Invoke(this, e);
    }

    sealed class Ring : IList<ConversationBlock>
    {
        ConversationBlock?[] slots = new ConversationBlock?[16];
        int head;
        int count;

        public long Shifted { get; private set; }

        public int Count => count;
        public bool IsReadOnly => false;

        int Slot(int index) => (head + index) % slots.Length;

        public ConversationBlock this[int index]
        {
            get { Within(index, count); return slots[Slot(index)]!; }
            set { Within(index, count); slots[Slot(index)] = value; }
        }

        public void Add(ConversationBlock item) => Insert(count, item);

        public void Insert(int index, ConversationBlock item)
        {
            Within(index, count + 1);
            if (count == slots.Length) { Grow(); }
            if (index < count - index)
            {
                // Room made in front: the blocks before it step back one.
                head = (head - 1 + slots.Length) % slots.Length;
                for (var i = 0; i < index; i++) { slots[Slot(i)] = slots[Slot(i + 1)]; }
                Shifted += index;
            }
            else
            {
                for (var i = count; i > index; i--) { slots[Slot(i)] = slots[Slot(i - 1)]; }
                Shifted += count - index;
            }
            slots[Slot(index)] = item;
            count++;
        }

        public void RemoveAt(int index)
        {
            Within(index, count);
            if (index < count - 1 - index)
            {
                // The gap closed from the front: the blocks before it step on one.
                for (var i = index; i > 0; i--) { slots[Slot(i)] = slots[Slot(i - 1)]; }
                slots[head] = null;
                head = (head + 1) % slots.Length;
                Shifted += index;
            }
            else
            {
                for (var i = index; i < count - 1; i++) { slots[Slot(i)] = slots[Slot(i + 1)]; }
                slots[Slot(count - 1)] = null;
                Shifted += count - 1 - index;
            }
            count--;
        }

        public bool Remove(ConversationBlock item)
        {
            var index = IndexOf(item);
            if (index < 0) { return false; }
            RemoveAt(index);
            return true;
        }

        public void Clear()
        {
            Array.Clear(slots);
            (head, count) = (0, 0);
        }

        public int IndexOf(ConversationBlock item)
        {
            for (var i = 0; i < count; i++)
            {
                if (EqualityComparer<ConversationBlock>.Default.Equals(slots[Slot(i)], item)) { return i; }
            }
            return -1;
        }

        public bool Contains(ConversationBlock item) => IndexOf(item) >= 0;

        public void CopyTo(ConversationBlock[] array, int arrayIndex)
        {
            for (var i = 0; i < count; i++) { array[arrayIndex + i] = slots[Slot(i)]!; }
        }

        public IEnumerator<ConversationBlock> GetEnumerator()
        {
            for (var i = 0; i < count; i++) { yield return slots[Slot(i)]!; }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        void Grow()
        {
            var grown = new ConversationBlock?[slots.Length * 2];
            for (var i = 0; i < count; i++) { grown[i] = slots[Slot(i)]; }
            (slots, head) = (grown, 0);
        }

        static void Within(int index, int limit)
        {
            if ((uint)index >= (uint)limit) { throw new ArgumentOutOfRangeException(nameof(index)); }
        }
    }
}
