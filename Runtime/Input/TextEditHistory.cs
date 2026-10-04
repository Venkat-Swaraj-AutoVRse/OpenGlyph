using System.Collections.Generic;

namespace LightSide
{
    /// <summary>
    /// Undo/redo of an input field: snapshots of (text, anchor, caret) taken before each edit group.
    /// Consecutive typing (or consecutive backspaces/deletes) at the caret coalesces into one group; a
    /// group ends when the edit kind changes, the caret moves by anything but the edit itself, a
    /// selection is replaced, a paste/cut/word-delete happens, a word ends (a space after a letter), or
    /// <see cref="GroupTimeout"/> seconds pass between edits.
    /// </summary>
    internal sealed class TextEditHistory
    {
        internal enum EditKind { None, Insert, Delete, Other }

        internal struct State
        {
            public string text;
            public int anchor, caret;
        }

        private readonly List<State> undo = new();
        private readonly List<State> redo = new();
        private EditKind groupKind = EditKind.None;
        private int groupCaret = -1;
        private float groupTime;

        /// <summary>Seconds between two edits after which a new group starts (0 = never by time).</summary>
        public float GroupTimeout = 1f;

        /// <summary>Maximum stored undo steps (oldest are dropped).</summary>
        public int Capacity = 128;

        public int UndoCount => undo.Count;
        public int RedoCount => redo.Count;

        public void Clear()
        {
            undo.Clear();
            redo.Clear();
            Break();
        }

        /// <summary>Ends the current typing group (caret moved, focus changed, ...).</summary>
        public void Break()
        {
            groupKind = EditKind.None;
            groupCaret = -1;
        }

        /// <summary>
        /// Call before applying an edit. <paramref name="before"/> is the state before it, <paramref name="caretAfter"/>
        /// where the caret will be after it. Coalesces with the open group when it continues it.
        /// </summary>
        public void Record(in State before, EditKind kind, int caretAfter, float now, bool breaksWord = false)
        {
            var continues = kind != EditKind.Other && kind == groupKind && before.caret == groupCaret &&
                            before.anchor == before.caret && !breaksWord &&
                            (GroupTimeout <= 0f || now - groupTime <= GroupTimeout);
            if (!continues)
            {
                undo.Add(before);
                if (undo.Count > Capacity) undo.RemoveAt(0);
            }
            redo.Clear();
            groupKind = kind == EditKind.Other ? EditKind.None : kind;
            groupCaret = caretAfter;
            groupTime = now;
        }

        public bool TryUndo(in State current, out State restored)
        {
            Break();
            if (undo.Count == 0) { restored = current; return false; }
            restored = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            redo.Add(current);
            return true;
        }

        public bool TryRedo(in State current, out State restored)
        {
            Break();
            if (redo.Count == 0) { restored = current; return false; }
            restored = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            undo.Add(current);
            return true;
        }
    }
}
