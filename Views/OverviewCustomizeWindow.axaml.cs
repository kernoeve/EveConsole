using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using EveCortex.Models;
using EveCortex.ViewModels;
using ReactiveUI;

namespace EveCortex.Views;

public partial class OverviewCustomizeWindow : Window
{
    private const double Edge = 8;   // px hit-zone for edge resize
    private const double Gap  = 4;   // px gutter around each box

    private static readonly IBrush GridLine   = new SolidColorBrush(Color.Parse("#22222e"));
    private static readonly IBrush BoxFill     = new SolidColorBrush(Color.Parse("#18233a"));
    private static readonly IBrush BoxBorder   = new SolidColorBrush(Color.Parse("#3a5a8a"));
    private static readonly IBrush BoxText     = new SolidColorBrush(Color.Parse("#cfe0ff"));
    private static readonly IBrush RemoveFg    = new SolidColorBrush(Color.Parse("#e0a0a0"));
    private static readonly IBrush GhostOk     = new SolidColorBrush(Color.Parse("#3370ad47"));
    private static readonly IBrush GhostBad    = new SolidColorBrush(Color.Parse("#33e74c3c"));
    private static readonly IBrush GhostOkPen  = new SolidColorBrush(Color.Parse("#70ad47"));
    private static readonly IBrush GhostBadPen = new SolidColorBrush(Color.Parse("#e74c3c"));

    private OverviewCustomizeViewModel? _vm;
    private IDisposable? _sizeSub;

    // Active box drag/resize state
    private OverviewSectionEditVm? _dragSection;
    private bool _mL, _mR, _mT, _mB, _isMove;
    private int _sR, _sC, _sRS, _sCS;
    private Point _startCanvas;

    // Active palette drag state
    private OverviewSectionEditVm? _paletteDrag;
    private bool _ghostShow, _ghostValid;
    private int _ghostRow, _ghostCol;

    public OverviewCustomizeWindow()
    {
        InitializeComponent();

        EditorCanvas.SizeChanged      += (_, _) => Render();
        EditorCanvas.PointerMoved     += OnCanvasMoved;
        EditorCanvas.PointerReleased  += OnCanvasReleased;

        DataContextChanged += (_, _) =>
        {
            _sizeSub?.Dispose();
            _vm = DataContext as OverviewCustomizeViewModel;
            if (_vm is not null)
                _sizeSub = _vm.WhenAnyValue(x => x.Rows, x => x.Cols)
                    .Subscribe(_ => { _vm!.ClampToBounds(); Render(); });
            Render();
        };
    }

    private (double CellW, double CellH)? CellSize()
    {
        if (_vm is null) return null;
        double w = EditorCanvas.Bounds.Width, h = EditorCanvas.Bounds.Height;
        if (w <= 0 || h <= 0) return null;
        return (w / _vm.ColCount, h / _vm.RowCount);
    }

    // ── Rendering ────────────────────────────────────────────────────────────────
    private void Render()
    {
        if (_vm is null) return;
        EditorCanvas.Children.Clear();
        if (CellSize() is not { } cell) return;
        var (cw, ch) = cell;
        if (cw <= 0 || ch <= 0) return;

        double w = EditorCanvas.Bounds.Width, h = EditorCanvas.Bounds.Height;
        int cols = _vm.ColCount, rows = _vm.RowCount;

        // Grid lines
        for (int i = 0; i <= cols; i++)
            EditorCanvas.Children.Add(Line(i * cw, 0, 1, h));
        for (int j = 0; j <= rows; j++)
            EditorCanvas.Children.Add(Line(0, j * ch, w, 1));

        // Placed sections
        foreach (var sec in _vm.Sections)
        {
            if (!sec.Enabled) continue;
            var box = new Border
            {
                Width           = Math.Max(1, sec.ColSpan * cw - Gap),
                Height          = Math.Max(1, sec.RowSpan * ch - Gap),
                Background      = BoxFill,
                BorderBrush     = BoxBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Tag             = sec,
                Cursor          = new Cursor(StandardCursorType.SizeAll),
            };
            Canvas.SetLeft(box, (sec.Col - 1) * cw + Gap / 2);
            Canvas.SetTop(box,  (sec.Row - 1) * ch + Gap / 2);

            var grid  = new Grid();
            grid.Children.Add(new TextBlock
            {
                Text                = sec.Title,
                Foreground          = BoxText,
                FontSize            = 12,
                TextWrapping        = TextWrapping.Wrap,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(6),
            });
            var remove = new Button
            {
                Content             = "✕",
                FontSize            = 11,
                Padding             = new Thickness(5, 1),
                Background          = Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                Foreground          = RemoveFg,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                Cursor              = new Cursor(StandardCursorType.Hand),
            };
            var captured = sec;
            remove.Click          += (_, _) => { captured.Enabled = false; Render(); };
            remove.PointerPressed += (_, ev) => ev.Handled = true;   // don't start a drag
            grid.Children.Add(remove);
            box.Child = grid;

            box.PointerPressed += OnBoxPressed;
            box.PointerMoved   += OnBoxHover;
            EditorCanvas.Children.Add(box);
        }

        // Palette drop ghost
        if (_paletteDrag is not null && _ghostShow)
        {
            var ghost = new Border
            {
                Width            = Math.Max(1, cw - Gap),
                Height           = Math.Max(1, ch - Gap),
                Background       = _ghostValid ? GhostOk : GhostBad,
                BorderBrush      = _ghostValid ? GhostOkPen : GhostBadPen,
                BorderThickness  = new Thickness(1),
                CornerRadius     = new CornerRadius(3),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ghost, (_ghostCol - 1) * cw + Gap / 2);
            Canvas.SetTop(ghost,  (_ghostRow - 1) * ch + Gap / 2);
            EditorCanvas.Children.Add(ghost);
        }
    }

    private static Rectangle Line(double x, double y, double w, double h)
    {
        var r = new Rectangle { Width = w, Height = h, Fill = GridLine, IsHitTestVisible = false };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    // ── Box drag / resize ──────────────────────────────────────────────────────
    private void OnBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border box || box.Tag is not OverviewSectionEditVm sec) return;
        var p = e.GetPosition(box);
        var b = box.Bounds;
        _mL = p.X <= Edge; _mR = p.X >= b.Width - Edge;
        _mT = p.Y <= Edge; _mB = p.Y >= b.Height - Edge;
        _isMove = !(_mL || _mR || _mT || _mB);

        _dragSection = sec;
        _sR = sec.Row; _sC = sec.Col; _sRS = sec.RowSpan; _sCS = sec.ColSpan;
        _startCanvas = e.GetPosition(EditorCanvas);
        e.Pointer.Capture(EditorCanvas);
        e.Handled = true;
    }

    private void OnBoxHover(object? sender, PointerEventArgs e)
    {
        if (_dragSection is not null || _paletteDrag is not null) return;
        if (sender is not Border box) return;
        var p = e.GetPosition(box);
        var b = box.Bounds;
        bool l = p.X <= Edge, r = p.X >= b.Width - Edge, t = p.Y <= Edge, btm = p.Y >= b.Height - Edge;
        box.Cursor = new Cursor(
            (l || r) && (t || btm) ? StandardCursorType.SizeAll
            : l || r               ? StandardCursorType.SizeWestEast
            : t || btm             ? StandardCursorType.SizeNorthSouth
            :                        StandardCursorType.SizeAll);
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null || CellSize() is not { } cell) return;
        var (cw, ch) = cell;
        var cur = e.GetPosition(EditorCanvas);

        if (_dragSection is not null)
        {
            int dc = (int)Math.Round((cur.X - _startCanvas.X) / cw);
            int dr = (int)Math.Round((cur.Y - _startCanvas.Y) / ch);
            int row = _sR, col = _sC, rs = _sRS, cs = _sCS;
            if (_isMove) { row = _sR + dr; col = _sC + dc; }
            else
            {
                if (_mR) cs = _sCS + dc;
                if (_mL) { col = _sC + dc; cs = _sCS - dc; }
                if (_mB) rs = _sRS + dr;
                if (_mT) { row = _sR + dr; rs = _sRS - dr; }
            }
            if (rs >= 1 && cs >= 1 && _vm.Fits(_dragSection, row, col, rs, cs))
            {
                _dragSection.Row = row; _dragSection.Col = col;
                _dragSection.RowSpan = rs; _dragSection.ColSpan = cs;
                Render();
            }
            return;
        }

        if (_paletteDrag is not null)
        {
            bool inside = cur.X >= 0 && cur.Y >= 0
                       && cur.X <= EditorCanvas.Bounds.Width && cur.Y <= EditorCanvas.Bounds.Height;
            if (inside)
            {
                _ghostCol = Math.Clamp((int)(cur.X / cw) + 1, 1, _vm.ColCount);
                _ghostRow = Math.Clamp((int)(cur.Y / ch) + 1, 1, _vm.RowCount);
                _ghostValid = _vm.Fits(null, _ghostRow, _ghostCol, 1, 1);
                _ghostShow = true;
            }
            else _ghostShow = false;
            Render();
        }
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragSection is not null)
        {
            _dragSection = null;
        }
        else if (_paletteDrag is not null)
        {
            if (_ghostShow && _ghostValid)
            {
                _paletteDrag.Enabled = true;
                _paletteDrag.Row = _ghostRow; _paletteDrag.Col = _ghostCol;
                _paletteDrag.RowSpan = 1; _paletteDrag.ColSpan = 1;
            }
            _paletteDrag = null;
            _ghostShow = false;
        }
        e.Pointer.Capture(null);
        Render();
    }

    // ── Palette drag-to-add ──────────────────────────────────────────────────────
    private void OnPalettePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not OverviewSectionEditVm sec) return;
        _paletteDrag = sec;
        _ghostShow = false;
        e.Pointer.Capture(EditorCanvas);
        e.Handled = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApply(object? sender, RoutedEventArgs e)
        => Close((DataContext as OverviewCustomizeViewModel)?.Build());
}
