using System;
using System.Collections.Generic;
using AnotherMarkdown;
class FileVisitHistoryTests {
  static int checks;
  static void Check(bool value) { if (!value) throw new Exception("History assertion " + checks); checks++; }
  static IntPtr Id(int n) => new IntPtr(n);
  static void Main() {
    var open = new HashSet<IntPtr> { Id(1), Id(2), Id(3), Id(4) };
    Func<IntPtr,bool> live = id => open.Contains(id);
    var h = new FileVisitHistory();
    Check(!h.CanMove(-1,live) && !h.CanMove(1,live));
    h.Record(Id(1)); h.Record(Id(1)); h.Record(IntPtr.Zero);
    Check(!h.CanMove(-1,live));
    h.Record(Id(2)); h.Record(Id(3));
    Check(h.Move(-1,live)==Id(2)); h.Record(Id(2));
    Check(h.Move(-1,live)==Id(1));
    Check(h.Move(-1,live)==IntPtr.Zero);
    Check(h.Move(1,live)==Id(2)); Check(h.Move(1,live)==Id(3));
    Check(h.Move(1,live)==IntPtr.Zero);
    h.Move(-1,live); h.Record(Id(4));
    Check(!h.CanMove(1,live)); Check(h.Move(-1,live)==Id(2));
    open.Remove(Id(1)); Check(!h.CanMove(-1,live));
    h.Remove(Id(1)); Check(!h.CanMove(-1,live));
    h.Remove(Id(4)); Check(!h.CanMove(1,live));
    h.Record(Id(3)); h.Record(Id(2));
    Check(h.Move(-1,live)==Id(3)); Check(h.Move(-1,live)==Id(2));
    Check(h.Move(1,live)==Id(3)); h.Remove(Id(2));
    Check(!h.CanMove(-1,live) && !h.CanMove(1,live));
    for (int i=10;i<310;i++) { open.Add(Id(i)); h.Record(Id(i)); }
    int moves=0; while(h.Move(-1,live)!=IntPtr.Zero) moves++;
    Check(moves==255);
    Console.WriteLine("PASS file history: " + checks + " assertions");
  }
}
