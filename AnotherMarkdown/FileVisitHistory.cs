using System;
using System.Collections.Generic;
namespace AnotherMarkdown {
  internal sealed class FileVisitHistory {
    private readonly List<IntPtr> visits = new List<IntPtr>();
    private int cursor = -1;
    public void Record(IntPtr id) {
      if (id == IntPtr.Zero || (cursor >= 0 && visits[cursor] == id)) return;
      if (cursor + 1 < visits.Count) visits.RemoveRange(cursor + 1, visits.Count - cursor - 1);
      visits.Add(id); cursor = visits.Count - 1;
      if (visits.Count > 256) { visits.RemoveAt(0); cursor--; }
    }
    public void Remove(IntPtr id) {
      for (int i = visits.Count - 1; i >= 0; i--) if (visits[i] == id) { visits.RemoveAt(i); if (i <= cursor) cursor--; }
    }
    public bool CanMove(int direction, Func<IntPtr, bool> open) => Find(direction, open) >= 0;
    public IntPtr Move(int direction, Func<IntPtr, bool> open) {
      var next = Find(direction, open); if (next < 0) return IntPtr.Zero;
      cursor = next; return visits[cursor];
    }
    private int Find(int direction, Func<IntPtr, bool> open) {
      if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
      for (int i = cursor + direction; i >= 0 && i < visits.Count; i += direction)
        if ((cursor < 0 || visits[i] != visits[cursor]) && open(visits[i])) return i;
      return -1;
    }
  }
}
