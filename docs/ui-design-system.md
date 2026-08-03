# DeveMobileLPR UI design system

DeveMobileLPR uses a small application-level design system rather than styling each page independently. The MAUI equivalent of shared CSS lives in `Resources/Styles`: the light and dark color dictionaries own semantic colors, while `DesignSystem.xaml` owns spacing, typography, surfaces, controls, and purpose-specific variants. `App.xaml` loads the active palette alongside the shared design system.

## Rules for new UI

1. Use semantic resources such as `Primary`, `Surface`, `TextSecondary`, and `Danger`; do not repeat hex colors in page XAML.
2. Start scrollable pages with `PageScrollContent`, fixed/list pages with `PageGrid`, and details with `DetailPageGrid` plus `DetailScrollContent`.
3. Use `Card`, `CompactCard`, `HighlightCard`, or a named semantic variant. Do not recreate padding, corner radius, and stroke on each page.
4. Use a wrapping `FlexLayout` for static side-by-side groups and a stable `Grid` for recycled list cards. Do not change a recycled item's row or column structure during rotation.
5. Use `PageHeader` for page titles and loading state, and `SettingsToggleRow` for labelled switches.
6. Prefer implicit control styles for buttons, pickers, sliders, switches, search bars, and progress indicators. Add a named variant to the design system when a real semantic difference is needed.
7. Use layout `Spacing`, `RowSpacing`, and `ColumnSpacing` for sibling gaps. Reserve margins for an intentional exception such as an empty-state breathing area.
8. Validate both portrait and landscape. A page is not complete if primary actions become clipped, cards overlap, or list content is crowded out.

## Responsive behavior

Static metric, filter, and settings groups wrap through `FlexLayout`. Recycled `CollectionView` cards keep a stable `Grid` structure and use `MeasureAllItems` when their heights vary, avoiding stale Android item measurements after rotation. The Drive page has dedicated portrait and landscape start compositions because its camera-first workflow has stricter height constraints.

## Component boundary

Reusable visual grammar belongs in resources and controls. Page XAML should describe content and page-specific composition. Highly specialized drawing—camera overlays and the route canvas—stays in its renderer, but should use the same semantic palette and interaction sizing wherever practical.

When adding a repeated pattern, first check whether an existing style or control expresses it. If the pattern appears on more than one page, promote it to the design system instead of copying markup.
