# Native cells in every sample and scrolling performance

## The shared native mode

Enable **Native cells** beside the sample's search field. Every existing example uses
its same data, query, grouping, selection, layout, undo history and commands in both
modes. The setting stays enabled when switching examples or dataset size. The dedicated
Native controls and Adaptive cell editors examples still apply their explicit templates;
custom templates take precedence over the selected default presentation.

```csharp
using DataGridScene.Controls;

grid.CellRenderingMode = GridCellRenderingMode.NativeControls;
// Back to canvas cell content (explicit application templates remain active).
grid.CellRenderingMode = GridCellRenderingMode.Skia;
```

This API belongs to `DataGridScene.Controls`, not the sample. It can be configured
before assigning a table. Switching modes validates pending edits first and rejects the
switch if an editor is invalid. It does not reshape the view or clear history. The mode
is application presentation configuration, not a serialized delegate or new workspace
format. Restoring a saved workspace keeps the host's chosen cell mode.

Native display content uses ordinary Uno TextBlock, Border, ProgressBar, CheckBox and
Polyline elements, including model formatting, badges, progress and sparklines. F2 or a
double-click opens an ordinary native editor. Text-based numeric/date editing preserves
exact decimals, large integers and timestamp precision; applications may still opt into
the existing NumberBox, Slider and date-only CalendarDatePicker adapters.

Headers, grouping/detail rows, summaries and grid lines keep their shared Skia renderer.
"Native cells" means Uno controls in the visual tree; it is **not** a switch from Uno's
Skia platform host to browser DOM or OS widgets. It is not full DevExpress parity or a
claim that every chrome element has a second native implementation.

## Avoided work while scrolling

Previously recycling removed each outgoing control from its visual parent, then added
it again for the incoming row. Now the bounded idle pool keeps collapsed controls under
the same parent. A recycled element still receives a new identity/version-checked
buffer. Its previous buffer is deactivated before unbinding, so delayed events cannot
write to another row. Collapsed idle controls cannot paint, receive pointer input or
participate in focus traversal.

An outgoing viewport is temporarily available for reuse before trimming the **unused**
pool to 128 elements. This avoids discarding the 129th outgoing element only to create
a replacement immediately when the visible viewport itself contains more than 128
controls. The existing 4,096 active/retained-edit limit remains. Creation of genuinely
new templates or a larger viewport can still allocate controls. Idle elements are
removed when evicted, when configuration is cleared, or when the grid is disposed.

Position changes use retained TranslateTransforms rather than Canvas.Left/Top writes.
Unchanged full-cell widths/heights and clipping rectangles are not assigned again.
Partially clipped rows and frozen columns keep full control dimensions with a separate
clip. Small scrolls inside an unchanged set of visible rows do not rebind their values.
Focus/error outlines remain above opaque controls without shrinking the editor.

Counters expose tree attachments/detachments, size writes and translation writes in
addition to creation/reuse/binding/placement counters. They describe work performed by
this grid, not all internal work performed by Uno or the graphics driver. The browser
suite records them on a 100,000-record workload. Script timings include automation and
frame-settle overhead and must not be interpreted as GPU timings or input latency.

## Validation and boundaries

`tests/browser/native-modes.mjs` uses actual pointer/keyboard input to switch every
example between modes, compare preserved state, edit/undo, reject invalid mode changes,
activate checkboxes, exercise adaptive templates and visible scrollbar parts, check
translated hit targets and frozen clipping, and measure scrolling work. It also executes
nine code-first Uno API contracts in the actual WASM runtime. Existing scrollbar,
native-control, selection, workspace and other browser suites remain enabled.

Browser CI uses ANGLE/SwiftShader. A passing operation-count regression is not a
hardware GPU throughput guarantee, and desktop compilation is not a complete native
runtime matrix. Arbitrary application controls can still have expensive templates,
layout or event handlers. Unbound cell display callbacks remain trusted application
code. Seamless server mode, automatic heterogeneous row measurement, full accessibility
providers, row transactions, recursive editable details and complete printing are still
tracked separately in the compatibility matrix.

Design references: [DevExpress optimized display/edit mode](https://docs.devexpress.com/WPF/17112/controls-and-libraries/data-grid/performance-improvement/optimized-mode)
and [Uno visual-tree performance guidance](https://platform.uno/docs/articles/Uno-UI-Performance.html).
No vendor source, branding or proprietary assets are used.

## Input review

Display-cell drag selection uses the same grid selection logic as canvas cells, while
pointer capture stays with the originating native host. Transferring capture to a
sibling canvas splits the native gesture recognizer's press/release sequence and can
prevent double-click editing. Recycled gestures are checked against the actual record
and column identity, not just the reused visual. Right-clicks on lightweight displays
open the same grid commands. Interactive child controls retain their own native input.

Successful no-op commits from unrelated controls must not clear another cell's active
validation error. The failed cell keeps its pending text, message and outline until
correction or cancellation. Error-free scrolling does not scan other controls for
validation errors.
