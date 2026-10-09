# DataGridScene

Independent, reusable .NET data-grid libraries and an Uno Platform desktop/WebAssembly sample. The behavioral reference is the publicly documented DevExpress WPF GridControl. This project does not contain or depend on DevExpress source, binaries, branding, or assets and is not affiliated with DevExpress.

## Implementation status

Implementation and validation are in progress. Full behavioral, visual, API, and accessibility parity has **not** been established. Feature support and reproducible validation evidence will be tracked in this repository; unsupported features must not be represented as implemented.

## Architecture

- `DataGridScene.Core`: schema, filtering, sorting, grouping, summaries, selection and transactional editing.
- `DataGridScene.Data`: local/remote data adapters, paging and cancellation.
- `DataGridScene.Layout`: viewport virtualization, variable row heights, column geometry and versioned layout persistence.
- `DataGridScene.Rendering.Skia`: renderer independent of Uno controls.
- `DataGridScene.Controls`: reusable Uno Platform GPU-composited grid and editors.
- `DataGridScene.Export`: data-aware exports.
- `DataGridScene.Demo`: shared Uno desktop and browser application.

Public references: [WPF GridControl](https://docs.devexpress.com/WPF/6084/controls-and-libraries/data-grid), [Uno SKCanvasElement](https://platform.uno/docs/articles/controls/SKCanvasElement.html), [Uno Skia renderer](https://platform.uno/docs/articles/features/using-skia-rendering.html).
