# Terminal.Gui 1.19.0

The C# sources and resources in this directory are vendored from upstream tag
`v1.19.0`, commit `79e2d14b68f07ed38c8f1f71aca1af7ab16d8d39`:
https://github.com/tui-cs/Terminal.Gui/tree/79e2d14b68f07ed38c8f1f71aca1af7ab16d8d39/Terminal.Gui

See LICENSE for the upstream MIT license. The project file builds those sources
for this application's .NET 10 target with the same NStack.Core and
System.Management dependencies. Tests have internal access to exercise the actual
input pump without a physical keyboard. Unchanged upstream code is retained so
builds do not depend on runtime reflection or binary patching.

## Local fix: ConsoleDrivers/NetDriver.cs

Both input queues are shared between worker threads and the UI thread. The
original Queue<T> accesses were unsynchronized. In NetInputHandler, the UI could
consume an item between Count and Peek/Dequeue, throwing Queue empty on the
unobserved worker task. The modal preview stayed visible but all keyboard input
stopped. A live process stack showed the UI waiting for input with no active
input reader. InputQueueTests reproduced the worker failure on the original code.

- Use ConcurrentQueue<T> and atomic TryDequeue; discard null reads before enqueue.
- Use cancellation tokens for input-worker event waits so shutdown wakes them.
- Allow an internal input-source delegate and expose its Task to regression tests.

InputQueueTests exercises 30,000 ordered events, empty reads, nested dispatch and
worker shutdown. Compare all other .cs/.resx files directly with the pinned tag
when updating this dependency.
