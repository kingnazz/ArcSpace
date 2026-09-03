using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArcSpace.Models;

namespace ArcSpace.Controls;

public sealed class FolderTreemap : FrameworkElement
{
    private const int MaximumItems = 48;
    private const double OuterPadding = 4d;
    private const double TileGap = 1.5d;

    private static readonly Brush BackgroundBrush = FrozenBrush(Color.FromRgb(15, 23, 42));
    private static readonly Brush EmptyTextBrush = FrozenBrush(Color.FromRgb(148, 163, 184));
    private static readonly Brush SecondaryTextBrush = FrozenBrush(Color.FromArgb(220, 255, 255, 255));
    private static readonly Brush HoverOverlayBrush = FrozenBrush(Color.FromArgb(28, 255, 255, 255));
    private static readonly Pen TileBorderPen = FrozenPen(Color.FromArgb(180, 15, 23, 42), 1d);
    private static readonly Pen SelectedBorderPen = FrozenPen(Color.FromRgb(255, 255, 255), 3d);
    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);
    private static readonly Typeface DetailTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private static readonly Brush[] Palette =
    [
        FrozenBrush(Color.FromRgb(37, 99, 235)),
        FrozenBrush(Color.FromRgb(14, 165, 233)),
        FrozenBrush(Color.FromRgb(20, 184, 166)),
        FrozenBrush(Color.FromRgb(34, 197, 94)),
        FrozenBrush(Color.FromRgb(234, 179, 8)),
        FrozenBrush(Color.FromRgb(249, 115, 22)),
        FrozenBrush(Color.FromRgb(239, 68, 68)),
        FrozenBrush(Color.FromRgb(139, 92, 246)),
        FrozenBrush(Color.FromRgb(236, 72, 153)),
        FrozenBrush(Color.FromRgb(100, 116, 139))
    ];

    private readonly List<Tile> _tiles = [];
    private readonly List<ScanItem> _observedItems = [];
    private INotifyCollectionChanged? _observedCollection;
    private ScanItem? _selectedItem;
    private ScanItem? _hoveredItem;

    public FolderTreemap()
    {
        Focusable = true;
        ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 175);
        ToolTipService.SetBetweenShowDelay(this, 50);
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(FolderTreemap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText),
        typeof(string),
        typeof(FolderTreemap),
        new FrameworkPropertyMetadata(
            "Start a scan to build the space map.",
            FrameworkPropertyMetadataOptions.AffectsRender));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public ScanItem? SelectedItem => _selectedItem;

    public event EventHandler<TreemapItemEventArgs>? ItemSelected;
    public event EventHandler<TreemapItemEventArgs>? ItemInvoked;

    public void SelectItem(ScanItem? item)
    {
        var match = item is null
            ? null
            : SnapshotItems().FirstOrDefault(candidate =>
                ReferenceEquals(candidate, item) ||
                string.Equals(candidate.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        SetSelectedItem(match, notify: false);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var surface = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        drawingContext.DrawRectangle(BackgroundBrush, null, surface);
        _tiles.Clear();

        var bounds = new Rect(
            OuterPadding,
            OuterPadding,
            Math.Max(0, surface.Width - (OuterPadding * 2)),
            Math.Max(0, surface.Height - (OuterPadding * 2)));

        var items = SnapshotItems();
        if (items.Count == 0 || bounds.Width < 24 || bounds.Height < 24)
        {
            DrawEmptyState(drawingContext, surface);
            return;
        }

        _tiles.AddRange(CreateLayout(items, bounds));
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var tile in _tiles)
        {
            DrawTile(drawingContext, tile, pixelsPerDip);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var item = FindItem(e.GetPosition(this));
        if (ReferenceEquals(item, _hoveredItem))
        {
            return;
        }

        _hoveredItem = item;
        Cursor = item is null ? Cursors.Arrow : Cursors.Hand;
        ToolTip = item is null
            ? null
            : $"{item.Name}\n{item.SizeDisplay} · {item.FileCountDisplay} files\n{item.FullPath}";
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredItem = null;
        Cursor = Cursors.Arrow;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var item = FindItem(e.GetPosition(this));
        if (item is null)
        {
            return;
        }

        Focus();
        SetSelectedItem(item, notify: true);
        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            ItemInvoked?.Invoke(this, new TreemapItemEventArgs(item));
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var item = FindItem(e.GetPosition(this));
        if (item is not null)
        {
            Focus();
            SetSelectedItem(item, notify: true);
        }

        base.OnMouseRightButtonDown(e);
    }

    private void SetSelectedItem(ScanItem? item, bool notify)
    {
        if (ReferenceEquals(item, _selectedItem))
        {
            return;
        }

        _selectedItem = item;
        InvalidateVisual();

        if (notify && item is not null)
        {
            ItemSelected?.Invoke(this, new TreemapItemEventArgs(item));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Enter && _selectedItem is not null)
        {
            ItemInvoked?.Invoke(this, new TreemapItemEventArgs(_selectedItem));
            e.Handled = true;
        }
    }

    private IReadOnlyList<ScanItem> SnapshotItems()
        => ItemsSource?
            .OfType<ScanItem>()
            .Where(item => item.SizeBytes > 0)
            .OrderByDescending(item => item.SizeBytes)
            .Take(MaximumItems)
            .ToList()
           ?? [];

    private void DrawTile(DrawingContext drawingContext, Tile tile, double pixelsPerDip)
    {
        var rect = Deflate(tile.Bounds, TileGap / 2d);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var fill = Palette[GetPaletteIndex(tile.Item.FullPath)];
        var pen = ReferenceEquals(tile.Item, _selectedItem) ? SelectedBorderPen : TileBorderPen;
        drawingContext.DrawRectangle(fill, pen, rect);

        if (ReferenceEquals(tile.Item, _hoveredItem))
        {
            drawingContext.DrawRectangle(HoverOverlayBrush, null, rect);
        }

        if (rect.Width < 58 || rect.Height < 28)
        {
            return;
        }

        var textBounds = Deflate(rect, 7d);
        if (textBounds.Width <= 8 || textBounds.Height <= 8)
        {
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(textBounds));

        var label = new FormattedText(
            tile.Item.Name,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            11.5,
            Brushes.White,
            pixelsPerDip)
        {
            MaxTextWidth = textBounds.Width,
            MaxTextHeight = Math.Min(30d, textBounds.Height),
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawingContext.DrawText(label, textBounds.TopLeft);

        if (rect.Height >= 46)
        {
            var detail = new FormattedText(
                tile.Item.SizeDisplay,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                DetailTypeface,
                10d,
                SecondaryTextBrush,
                pixelsPerDip)
            {
                MaxTextWidth = textBounds.Width,
                MaxTextHeight = 18d,
                Trimming = TextTrimming.CharacterEllipsis
            };
            drawingContext.DrawText(detail, new Point(textBounds.Left, textBounds.Top + 18d));
        }

        drawingContext.Pop();
    }

    private void DrawEmptyState(DrawingContext drawingContext, Rect surface)
    {
        if (surface.Width < 20 || surface.Height < 20)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            EmptyText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            DetailTypeface,
            12d,
            EmptyTextBrush,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(0, surface.Width - 40d),
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var origin = new Point(
            Math.Max(20d, (surface.Width - text.MaxTextWidth) / 2d),
            Math.Max(0, (surface.Height - text.Height) / 2d));
        drawingContext.DrawText(text, origin);
    }

    private ScanItem? FindItem(Point point)
    {
        for (var index = _tiles.Count - 1; index >= 0; index--)
        {
            if (_tiles[index].Bounds.Contains(point))
            {
                return _tiles[index].Item;
            }
        }

        return null;
    }

    private static IReadOnlyList<Tile> CreateLayout(IReadOnlyList<ScanItem> items, Rect bounds)
    {
        var totalSize = items.Sum(item => (double)item.SizeBytes);
        if (totalSize <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return [];
        }

        var totalArea = bounds.Width * bounds.Height;
        var remainingItems = items
            .Select(item => new WeightedItem(item, item.SizeBytes / totalSize * totalArea))
            .ToList();

        var tiles = new List<Tile>(remainingItems.Count);
        var row = new List<WeightedItem>();
        var remainingBounds = bounds;
        var itemIndex = 0;

        while (itemIndex < remainingItems.Count && remainingBounds.Width > 0 && remainingBounds.Height > 0)
        {
            var next = remainingItems[itemIndex];
            var shortSide = Math.Min(remainingBounds.Width, remainingBounds.Height);

            if (row.Count == 0 || WorstAspect(row, next.Area, shortSide) <= WorstAspect(row, null, shortSide))
            {
                row.Add(next);
                itemIndex++;
                continue;
            }

            LayoutRow(row, ref remainingBounds, tiles);
            row.Clear();
        }

        if (row.Count > 0 && remainingBounds.Width > 0 && remainingBounds.Height > 0)
        {
            LayoutRow(row, ref remainingBounds, tiles);
        }

        return tiles;
    }

    private static double WorstAspect(IReadOnlyList<WeightedItem> row, double? candidateArea, double side)
    {
        if (side <= 0)
        {
            return double.MaxValue;
        }

        var sum = candidateArea ?? 0d;
        var minimum = candidateArea ?? double.MaxValue;
        var maximum = candidateArea ?? 0d;

        foreach (var item in row)
        {
            sum += item.Area;
            minimum = Math.Min(minimum, item.Area);
            maximum = Math.Max(maximum, item.Area);
        }

        if (sum <= 0 || minimum <= 0)
        {
            return double.MaxValue;
        }

        var sideSquared = side * side;
        var sumSquared = sum * sum;
        return Math.Max(
            sideSquared * maximum / sumSquared,
            sumSquared / (sideSquared * minimum));
    }

    private static void LayoutRow(
        IReadOnlyList<WeightedItem> row,
        ref Rect remainingBounds,
        ICollection<Tile> tiles)
    {
        var rowArea = row.Sum(item => item.Area);
        if (rowArea <= 0)
        {
            return;
        }

        if (remainingBounds.Width >= remainingBounds.Height)
        {
            // In a wide region, consume a vertical strip so the row is laid out
            // against the short side used by the aspect-ratio calculation.
            var rowWidth = Math.Min(remainingBounds.Width, rowArea / remainingBounds.Height);
            var y = remainingBounds.Top;

            foreach (var item in row)
            {
                var height = rowWidth <= 0 ? 0 : item.Area / rowWidth;
                tiles.Add(new Tile(item.Item, new Rect(remainingBounds.Left, y, rowWidth, height)));
                y += height;
            }

            remainingBounds = new Rect(
                remainingBounds.Left + rowWidth,
                remainingBounds.Top,
                Math.Max(0, remainingBounds.Width - rowWidth),
                remainingBounds.Height);
        }
        else
        {
            // In a tall region, consume a horizontal strip for the same reason.
            var rowHeight = Math.Min(remainingBounds.Height, rowArea / remainingBounds.Width);
            var x = remainingBounds.Left;

            foreach (var item in row)
            {
                var width = rowHeight <= 0 ? 0 : item.Area / rowHeight;
                tiles.Add(new Tile(item.Item, new Rect(x, remainingBounds.Top, width, rowHeight)));
                x += width;
            }

            remainingBounds = new Rect(
                remainingBounds.Left,
                remainingBounds.Top + rowHeight,
                remainingBounds.Width,
                Math.Max(0, remainingBounds.Height - rowHeight));
        }
    }

    private static Rect Deflate(Rect rect, double amount)
        => new(
            rect.X + amount,
            rect.Y + amount,
            Math.Max(0, rect.Width - (amount * 2d)),
            Math.Max(0, rect.Height - (amount * 2d)));

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (FolderTreemap)dependencyObject;
        control.AttachToItemsSource(args.OldValue as IEnumerable, args.NewValue as IEnumerable);
    }

    private void AttachToItemsSource(IEnumerable? oldSource, IEnumerable? newSource)
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= ItemsSource_CollectionChanged;
        }

        StopObservingAllItems();
        _observedCollection = newSource as INotifyCollectionChanged;
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged += ItemsSource_CollectionChanged;
        }

        ObserveCurrentItems(newSource);
        _selectedItem = null;
        _hoveredItem = null;
        InvalidateVisual();
    }

    private void ItemsSource_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                ObserveItems(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                StopObservingItems(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                StopObservingItems(e.OldItems);
                ObserveItems(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                StopObservingAllItems();
                ObserveCurrentItems(ItemsSource);
                break;
            case NotifyCollectionChangedAction.Move:
                break;
        }

        if (_selectedItem is not null && !_observedItems.Contains(_selectedItem))
        {
            _selectedItem = null;
        }

        if (_hoveredItem is not null && !_observedItems.Contains(_hoveredItem))
        {
            _hoveredItem = null;
            ToolTip = null;
        }

        InvalidateVisual();
    }

    private void ObserveCurrentItems(IEnumerable? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var item in source
                     .OfType<ScanItem>()
                     .Where(item => item.SizeBytes > 0)
                     .OrderByDescending(item => item.SizeBytes)
                     .Take(MaximumItems))
        {
            ObserveItem(item);
        }
    }

    private void ObserveItems(IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.OfType<ScanItem>())
        {
            ObserveItem(item);
        }
    }

    private void ObserveItem(ScanItem item)
    {
        if (_observedItems.Contains(item))
        {
            return;
        }

        item.PropertyChanged += Item_PropertyChanged;
        _observedItems.Add(item);
    }

    private void StopObservingItems(IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.OfType<ScanItem>())
        {
            item.PropertyChanged -= Item_PropertyChanged;
            _observedItems.Remove(item);
        }
    }

    private void StopObservingAllItems()
    {
        foreach (var item in _observedItems)
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }

        _observedItems.Clear();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScanItem.SizeBytes) or nameof(ScanItem.FileCount) or null)
        {
            InvalidateVisual();
        }
    }

    private static int GetPaletteIndex(string path)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in path)
            {
                hash ^= char.ToUpperInvariant(character);
                hash *= 16777619;
            }

            return (int)(hash % (uint)Palette.Length);
        }
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private sealed record WeightedItem(ScanItem Item, double Area);
    private sealed record Tile(ScanItem Item, Rect Bounds);
}

public sealed class TreemapItemEventArgs(ScanItem item) : EventArgs
{
    public ScanItem Item { get; } = item;
}
