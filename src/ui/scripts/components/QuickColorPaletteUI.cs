using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Painter;

namespace DeadlockPlayground.UI
{
    public partial class QuickColorPaletteUI : PanelContainer
    {
        [Signal] public delegate void ColorSelectedEventHandler(Color color);

        [ExportGroup("UI References")]
        [Export] private HBoxContainer _swatchContainer;
        [Export] private Button _btnSwatch0;
        [Export] private Button _btnSwatch1;
        [Export] private Button _btnSwatch2;
        [Export] private Button _btnSwatch3;
        [Export] private Button _btnSwatch4;
        [Export] private Button _btnExpand;

        [ExportGroup("Popup References")]
        [Export] private PanelContainer _popupPanel;
        [Export] private Label _lblTitle;
        [Export] private Button _btnClosePopup;
        [Export] private GridContainer _recentGrid;
        [Export] private Button _btnPinCurrent;
        [Export] private GridContainer _favoritesGrid;

        private MeshPainter3D _painter;
        private FloatingBrushPaletteUI _brushPalette;
        private Button _btnOpenUVCanvas;

        private readonly Button[] _recentSwatchButtons = new Button[5];
        private readonly bool[] _swatchIsHovered = new bool[5];

        private readonly List<Color> _recentColors = new();
        private readonly List<Color> _favoriteColors = new();
        private Color _activeColor = new Color(0.85f, 0.15f, 0.20f, 1.0f);

        public bool IsPopupOpen => _popupPanel != null && _popupPanel.Visible;

        public override void _Ready()
        {
            Name = "QuickColorPalette";
            MouseFilter = MouseFilterEnum.Stop;
            ZIndex = 15;

            ResolveSceneNodes();
            ConnectEvents();
            InitializeDefaultColors();
            RefreshSwatches();
            RefreshPopupGrids();
        }

        private void ResolveSceneNodes()
        {
            _swatchContainer ??= GetNodeOrNull<HBoxContainer>("SwatchContainer");
            _btnSwatch0 ??= GetNodeOrNull<Button>("SwatchContainer/Swatch0");
            _btnSwatch1 ??= GetNodeOrNull<Button>("SwatchContainer/Swatch1");
            _btnSwatch2 ??= GetNodeOrNull<Button>("SwatchContainer/Swatch2");
            _btnSwatch3 ??= GetNodeOrNull<Button>("SwatchContainer/Swatch3");
            _btnSwatch4 ??= GetNodeOrNull<Button>("SwatchContainer/Swatch4");
            _btnExpand ??= GetNodeOrNull<Button>("SwatchContainer/BtnExpand");

            _recentSwatchButtons[0] = _btnSwatch0;
            _recentSwatchButtons[1] = _btnSwatch1;
            _recentSwatchButtons[2] = _btnSwatch2;
            _recentSwatchButtons[3] = _btnSwatch3;
            _recentSwatchButtons[4] = _btnSwatch4;

            _popupPanel ??= GetNodeOrNull<PanelContainer>("ColorPalettePopup");
            _lblTitle ??= GetNodeOrNull<Label>("ColorPalettePopup/VBox/Header/Title");
            _btnClosePopup ??= GetNodeOrNull<Button>("ColorPalettePopup/VBox/Header/BtnClose");
            _recentGrid ??= GetNodeOrNull<GridContainer>("ColorPalettePopup/VBox/RecentGrid");
            _btnPinCurrent ??= GetNodeOrNull<Button>("ColorPalettePopup/VBox/FavoritesHeader/BtnPin");
            _favoritesGrid ??= GetNodeOrNull<GridContainer>("ColorPalettePopup/VBox/FavoritesGrid");
        }

        private void ConnectEvents()
        {
            for (int i = 0; i < 5; i++)
            {
                int index = i;
                var btn = _recentSwatchButtons[i];
                if (btn != null)
                {
                    btn.MouseEntered += () => { _swatchIsHovered[index] = true; btn.QueueRedraw(); };
                    btn.MouseExited += () => { _swatchIsHovered[index] = false; btn.QueueRedraw(); };
                    btn.Draw += () => DrawSwatch(index);
                    btn.Pressed += () => OnSwatchClicked(index);
                }
            }

            if (_btnExpand != null)
            {
                _btnExpand.Pressed += TogglePopup;
            }

            if (_btnClosePopup != null)
            {
                _btnClosePopup.Pressed += () =>
                {
                    if (_popupPanel != null) _popupPanel.Visible = false;
                };
            }

            if (_btnPinCurrent != null)
            {
                _btnPinCurrent.Pressed += () => PinColor(_activeColor);
            }
        }

        private void InitializeDefaultColors()
        {
            _recentColors.Clear();
            // Default Deadlock Playground brush color (Crimson / Red) as primary default swatch
            _recentColors.Add(new Color(0.85f, 0.15f, 0.20f, 1.0f));
            _recentColors.Add(Color.FromHtml("#FB8C00")); // Amber
            _recentColors.Add(Color.FromHtml("#43A047")); // Emerald
            _recentColors.Add(Color.FromHtml("#1E88E5")); // Cobalt
            _recentColors.Add(Color.FromHtml("#F5F5F5")); // Off-White

            _favoriteColors.Clear();
            _favoriteColors.Add(new Color(0.85f, 0.15f, 0.20f, 1.0f));
            _favoriteColors.Add(Color.FromHtml("#212121")); // Charcoal Black
            _favoriteColors.Add(Color.FromHtml("#FDD835")); // Warm Yellow
            _favoriteColors.Add(Color.FromHtml("#8E24AA")); // Vivid Purple
            _favoriteColors.Add(Color.FromHtml("#00ACC1")); // Cyan Teal
            _favoriteColors.Add(Color.FromHtml("#D81B60")); // Magenta
        }

        private void DrawSwatch(int index)
        {
            var btn = _recentSwatchButtons[index];
            if (btn == null || index >= _recentColors.Count) return;

            Vector2 size = btn.Size;
            if (size.X <= 2.0f || size.Y <= 2.0f)
            {
                size = btn.CustomMinimumSize;
            }
            Vector2 center = size * 0.5f;
            float radius = (MathF.Min(size.X, size.Y) * 0.5f) - 2.5f;
            if (radius <= 1.0f) return;

            Color col = _recentColors[index];
            bool isActive = IsColorSimilar(col, _activeColor);
            bool isHovered = _swatchIsHovered[index];

            // 1. Dark drop shadow for contrast against light or dark backgrounds
            btn.DrawCircle(center + new Vector2(0, 1.0f), radius + 1.2f, new Color(0, 0, 0, 0.45f));

            // 2. Swatch color fill
            btn.DrawCircle(center, radius, col);

            // 3. Selection / status ring
            if (isActive)
            {
                // Glowing gold ring for the currently active color
                btn.DrawArc(center, radius + 1.5f, 0, Mathf.Tau, 36, new Color(1.0f, 0.85f, 0.22f, 1.0f), 2.2f, true);
                // Inner dark contrast ring
                btn.DrawArc(center, radius - 0.5f, 0, Mathf.Tau, 36, new Color(0.0f, 0.0f, 0.0f, 0.45f), 1.0f, true);
            }
            else if (isHovered)
            {
                // Bright white hover ring
                btn.DrawArc(center, radius + 1.0f, 0, Mathf.Tau, 36, new Color(1.0f, 1.0f, 1.0f, 0.95f), 1.8f, true);
            }
            else
            {
                // Subtle clean perimeter
                btn.DrawArc(center, radius, 0, Mathf.Tau, 32, new Color(1.0f, 1.0f, 1.0f, 0.35f), 1.0f, true);
            }
        }

        private bool _isPainterConnected = false;
        private bool _isBtnVisibilityConnected = false;

        public void ClosePopup()
        {
            if (_popupPanel != null)
            {
                _popupPanel.Visible = false;
            }
        }

        private void DisconnectEvents()
        {
            if (_isPainterConnected && _painter != null && GodotObject.IsInstanceValid(_painter))
            {
                _painter.BrushColorChanged -= OnPainterBrushColorChanged;
                _painter.ColorSampled -= OnColorSampled;
                _isPainterConnected = false;
            }
            if (_isBtnVisibilityConnected && _btnOpenUVCanvas != null && GodotObject.IsInstanceValid(_btnOpenUVCanvas))
            {
                _btnOpenUVCanvas.VisibilityChanged -= UpdateBarPosition;
                _isBtnVisibilityConnected = false;
            }
        }

        public void Setup(MeshPainter3D painter, FloatingBrushPaletteUI brushPalette, Button btnUV)
        {
            DisconnectEvents();

            _painter = painter;
            _brushPalette = brushPalette;
            _btnOpenUVCanvas = btnUV;

            if (_painter != null && GodotObject.IsInstanceValid(_painter))
            {
                _painter.BrushColorChanged += OnPainterBrushColorChanged;
                _painter.ColorSampled += OnColorSampled;
                _painter.QuickColorPalette = this;
                _isPainterConnected = true;
                _activeColor = _painter.BrushColor;
                AddRecentColor(_activeColor);
            }

            if (_btnOpenUVCanvas != null && GodotObject.IsInstanceValid(_btnOpenUVCanvas))
            {
                _btnOpenUVCanvas.VisibilityChanged += UpdateBarPosition;
                _isBtnVisibilityConnected = true;
            }

            UpdateBarPosition();
            RefreshSwatches();
            RefreshPopupGrids();
        }

        private void OnColorSampled(Color color)

        {
            AddRecentColor(color);
        }

        private void OnPainterBrushColorChanged(Color color)
        {
            SetActiveColor(color, commitToRecents: false);
        }

        public void SetActiveColor(Color color, bool commitToRecents = false)
        {
            _activeColor = color;
            if (commitToRecents)
            {
                AddRecentColor(color);
            }
            else
            {
                RefreshSwatches();
                RefreshPopupGrids();
            }
        }

        public void AddRecentColor(Color color)
        {
            _activeColor = color;

            // Deduplicate near-identical colors
            for (int i = _recentColors.Count - 1; i >= 0; i--)
            {
                if (IsColorSimilar(_recentColors[i], color))
                {
                    _recentColors.RemoveAt(i);
                }
            }

            _recentColors.Insert(0, color);

            // Cap at 15
            while (_recentColors.Count > 15)
            {
                _recentColors.RemoveAt(_recentColors.Count - 1);
            }

            RefreshSwatches();
            RefreshPopupGrids();
        }

        public List<string> GetPaletteHistory()
        {
            var list = new List<string>();
            foreach (var col in _recentColors)
            {
                list.Add("#" + col.ToHtml(false).ToLowerInvariant());
            }
            return list;
        }

        public void SetPaletteHistory(IEnumerable<string> hexColors)
        {
            if (hexColors == null) return;
            _recentColors.Clear();
            foreach (var hex in hexColors)
            {
                if (string.IsNullOrWhiteSpace(hex)) continue;
                try
                {
                    string cleanHex = hex.Trim();
                    if (!cleanHex.StartsWith("#")) cleanHex = "#" + cleanHex;
                    Color c = Color.FromHtml(cleanHex);
                    _recentColors.Add(c);
                }
                catch { }
            }
            if (_recentColors.Count == 0)
            {
                InitializeDefaultColors();
            }
            if (_recentColors.Count > 0)
            {
                _activeColor = _recentColors[0];
            }
            RefreshSwatches();
            RefreshPopupGrids();
        }

        public void PinColor(Color color)
        {
            for (int i = 0; i < _favoriteColors.Count; i++)
            {
                if (IsColorSimilar(_favoriteColors[i], color)) return;
            }

            _favoriteColors.Insert(0, color);
            while (_favoriteColors.Count > 15)
            {
                _favoriteColors.RemoveAt(_favoriteColors.Count - 1);
            }

            RefreshPopupGrids();
        }

        public void UnpinColor(Color color)
        {
            for (int i = _favoriteColors.Count - 1; i >= 0; i--)
            {
                if (IsColorSimilar(_favoriteColors[i], color))
                {
                    _favoriteColors.RemoveAt(i);
                    break;
                }
            }
            RefreshPopupGrids();
        }

        public void SelectColor(Color color)
        {
            _activeColor = color;
            if (_painter != null)
            {
                _painter.BrushColor = color;
            }
            if (_brushPalette != null)
            {
                _brushPalette.SetUniversalColor(color, commitToRecents: false);
            }

            AddRecentColor(color);
            EmitSignal(SignalName.ColorSelected, color);
        }

        private void OnSwatchClicked(int index)
        {
            if (index >= 0 && index < _recentColors.Count)
            {
                SelectColor(_recentColors[index]);
            }
        }

        private void RefreshSwatches()
        {
            for (int i = 0; i < 5; i++)
            {
                var btn = _recentSwatchButtons[i];
                if (btn == null) continue;

                if (i < _recentColors.Count)
                {
                    Color col = _recentColors[i];
                    btn.Visible = true;
                    btn.TooltipText = $"#{col.ToHtml(false)} (Click to select)";
                    btn.QueueRedraw();
                }
                else
                {
                    btn.Visible = false;
                }
            }
        }

        private void RefreshPopupGrids()
        {
            if (_recentGrid == null || _favoritesGrid == null) return;

            // Clear old children
            foreach (Node child in _recentGrid.GetChildren())
            {
                _recentGrid.RemoveChild(child);
                child.QueueFree();
            }
            foreach (Node child in _favoritesGrid.GetChildren())
            {
                _favoritesGrid.RemoveChild(child);
                child.QueueFree();
            }

            // Populate recent up to 15
            for (int i = 0; i < _recentColors.Count; i++)
            {
                Color col = _recentColors[i];
                var btn = CreateGridSwatch(col, IsColorSimilar(col, _activeColor), () =>
                {
                    SelectColor(col);
                });
                _recentGrid.AddChild(btn);
            }

            // Populate favorites
            for (int i = 0; i < _favoriteColors.Count; i++)
            {
                Color col = _favoriteColors[i];
                var btn = CreateGridSwatch(col, IsColorSimilar(col, _activeColor), () =>
                {
                    SelectColor(col);
                });
                btn.GuiInput += (ev) =>
                {
                    if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                    {
                        UnpinColor(col);
                        btn.AcceptEvent();
                    }
                };
                btn.TooltipText = $"#{col.ToHtml(false)}\nLeft-click: Select\nRight-click: Unpin";
                _favoritesGrid.AddChild(btn);
            }
        }

        private Button CreateGridSwatch(Color col, bool isActive, Action onClick)
        {
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(22, 22),
                FocusMode = FocusModeEnum.None,
                Flat = false,
                MouseDefaultCursorShape = CursorShape.PointingHand,
                TooltipText = $"#{col.ToHtml(false)}"
            };

            var emptyStyle = new StyleBoxEmpty();
            btn.AddThemeStyleboxOverride("normal", emptyStyle);
            btn.AddThemeStyleboxOverride("hover", emptyStyle);
            btn.AddThemeStyleboxOverride("pressed", emptyStyle);
            btn.AddThemeStyleboxOverride("focus", emptyStyle);
            btn.AddThemeStyleboxOverride("disabled", emptyStyle);

            bool isHovered = false;
            btn.MouseEntered += () => { isHovered = true; btn.QueueRedraw(); };
            btn.MouseExited += () => { isHovered = false; btn.QueueRedraw(); };

            btn.Draw += () =>
            {
                Vector2 size = btn.Size;
                if (size.X <= 2.0f || size.Y <= 2.0f)
                {
                    size = btn.CustomMinimumSize;
                }
                Vector2 center = size * 0.5f;
                float radius = (MathF.Min(size.X, size.Y) * 0.5f) - 2.0f;
                if (radius <= 1.0f) return;

                btn.DrawCircle(center + new Vector2(0, 1.0f), radius + 1.0f, new Color(0, 0, 0, 0.40f));
                btn.DrawCircle(center, radius, col);

                if (isActive)
                {
                    btn.DrawArc(center, radius + 1.5f, 0, Mathf.Tau, 32, new Color(1.0f, 0.85f, 0.22f, 1.0f), 2.0f, true);
                    btn.DrawArc(center, radius - 0.5f, 0, Mathf.Tau, 32, new Color(0.0f, 0.0f, 0.0f, 0.40f), 1.0f, true);
                }
                else if (isHovered)
                {
                    btn.DrawArc(center, radius + 1.0f, 0, Mathf.Tau, 32, new Color(1.0f, 1.0f, 1.0f, 0.95f), 1.5f, true);
                }
                else
                {
                    btn.DrawArc(center, radius, 0, Mathf.Tau, 28, new Color(1.0f, 1.0f, 1.0f, 0.35f), 1.0f, true);
                }
            };

            btn.Pressed += onClick;
            return btn;
        }

        private void TogglePopup()
        {
            if (_popupPanel == null) return;
            bool nextVisible = !_popupPanel.Visible;
            _popupPanel.Visible = nextVisible;
            if (nextVisible)
            {
                Vector2 gPos = GlobalPosition;
                float posY = (Size.Y > 0 ? Size.Y : 32.0f) + 4;
                _popupPanel.GlobalPosition = new Vector2(gPos.X, gPos.Y + posY);
                RefreshPopupGrids();
            }
        }

        public void UpdateBarPosition()
        {
            if (!IsInsideTree()) return;

            float posX = 16.0f;
            float posY = 16.0f;

            if (_btnOpenUVCanvas != null && _btnOpenUVCanvas.IsVisibleInTree())
            {
                Rect2 uvRect = _btnOpenUVCanvas.GetRect();
                float uvWidth = uvRect.Size.X > 0 ? uvRect.Size.X : _btnOpenUVCanvas.CustomMinimumSize.X;
                float uvHeight = uvRect.Size.Y > 0 ? uvRect.Size.Y : _btnOpenUVCanvas.CustomMinimumSize.Y;
                float paletteH = Size.Y > 0 ? Size.Y : 32.0f;

                posX = uvRect.Position.X + uvWidth + 8.0f;
                posY = uvRect.Position.Y + Math.Max(0.0f, (uvHeight - paletteH) * 0.5f);
            }

            Position = new Vector2(posX, posY);

            if (IsPopupOpen && _popupPanel != null)
            {
                float paletteH = Size.Y > 0 ? Size.Y : 32.0f;
                _popupPanel.GlobalPosition = new Vector2(GlobalPosition.X, GlobalPosition.Y + paletteH + 4);
            }
        }

        public override void _Input(InputEvent @event)
        {
            if (IsPopupOpen && @event is InputEventMouseButton mb && mb.Pressed)
            {
                if (!GetPaletteGlobalRect().HasPoint(mb.Position) &&
                    !GetPopupGlobalRect().HasPoint(mb.Position))
                {
                    _popupPanel.Visible = false;
                }
            }
        }

        public bool IsMouseOverPalette(Vector2 screenPos)
        {
            if (!IsVisibleInTree()) return false;
            if (GetGlobalRect().HasPoint(screenPos)) return true;
            if (IsPopupOpen && _popupPanel != null && _popupPanel.GetGlobalRect().HasPoint(screenPos)) return true;
            return false;
        }

        public Rect2 GetPaletteGlobalRect()
        {
            return GetGlobalRect();
        }

        public Rect2 GetPopupGlobalRect()
        {
            return (IsPopupOpen && _popupPanel != null) ? _popupPanel.GetGlobalRect() : default;
        }

        private static bool IsColorSimilar(Color a, Color b)
        {
            return MathF.Abs(a.R - b.R) < 0.02f &&
                   MathF.Abs(a.G - b.G) < 0.02f &&
                   MathF.Abs(a.B - b.B) < 0.02f;
        }

        public override void _Notification(int what)
        {
            base._Notification(what);
            if (what == NotificationVisibilityChanged)
            {
                if (!Visible && _popupPanel != null)
                {
                    _popupPanel.Visible = false;
                }
            }
            else if (what == NotificationResized)
            {
                UpdateBarPosition();
            }
        }

        public override void _ExitTree()
        {
            DisconnectEvents();
            base._ExitTree();
        }

    }
}
