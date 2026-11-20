using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 사진 자르기 창: 회색 바탕에 그림이 놓이고, 3등분 선이 그어진 정사각형 틀을 끌어 옮기고 모서리를 끌어 키우고 줄이며(휠로도) 영역을 고른다.
/// [자르기 완료]를 누르면 CropRect(원본 그림 기준 영역)를 돌려준다. 정사각형이라 동그라미 안에 그대로 들어간다.
/// </summary>
internal sealed class PhotoCropDialog : Form
{
    private readonly CropBox box;
    private readonly Button done;

    /// <summary>고른 영역(원본 그림의 픽셀 좌표).</summary>
    public Rectangle CropRect => box.SourceCrop;

    public PhotoCropDialog(string file)
    {
        Text = "사진 자르기";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Font = new Font(NovelStore.UiFontFamily, 10f);

        Bitmap img;
        using (var ms = new MemoryStream(File.ReadAllBytes(file))) using (var src = Image.FromStream(ms)) img = new Bitmap(src);
        box = new CropBox(img) { Dock = DockStyle.Fill };
        var bottom = new Panel { Dock = DockStyle.Bottom, BackColor = Color.White };
        done = new Button { Text = "자르기 완료", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0x1B, 0x1B, 0x1D), ForeColor = Color.White, Font = new Font(NovelStore.UiFontFamily, 10.5f, FontStyle.Bold), Cursor = Cursors.Hand, DialogResult = DialogResult.OK };
        done.FlatAppearance.BorderSize = 0;
        bottom.Controls.Add(done);
        Controls.Add(box);
        Controls.Add(bottom);
        AcceptButton = done;
        Disposed += (_, _) => img.Dispose();

        // 이 앱은 크기를 직접 정한 창을 자동으로 키우지 않아서, 화면 배율에 맞춰 직접 키운다
        Load += (_, _) =>
        {
            int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
            ClientSize = new Size(S(520), S(500));
            bottom.Height = S(64);
            done.Size = new Size(S(130), S(40));
            done.Location = new Point(bottom.Width - done.Width - S(16), (bottom.Height - done.Height) / 2);
            done.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            box.ResetCrop();
        };
    }

    /// <summary>그림 + 틀을 그리고 끌기를 받는 칸.</summary>
    private sealed class CropBox : Control
    {
        private readonly Bitmap img;
        private Rectangle imgRect, crop;
        private enum Drag { None, Move, NW, NE, SW, SE }
        private Drag drag;
        private Point grab, anchor;
        private Rectangle grabCrop;

        public CropBox(Bitmap image)
        {
            img = image;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(0x80, 0x80, 0x80);
        }

        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        private float Scale => imgRect.Width / (float)img.Width;
        private int MinSide => S(40);

        /// <summary>처음 틀: 그림 안에서 가장 큰 정사각형(가운데).</summary>
        public void ResetCrop()
        {
            LayoutImage();
            int side = Math.Min(imgRect.Width, imgRect.Height);
            crop = new Rectangle(imgRect.X + (imgRect.Width - side) / 2, imgRect.Y + (imgRect.Height - side) / 2, side, side);
            Invalidate();
        }

        private void LayoutImage()
        {
            int pad = S(40);
            float sc = Math.Min((Width - pad * 2) / (float)img.Width, (Height - pad * 2) / (float)img.Height);
            sc = Math.Max(0.01f, sc);
            int w = (int)Math.Round(img.Width * sc), h = (int)Math.Round(img.Height * sc);
            imgRect = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        }

        protected override void OnResize(EventArgs e)
        {
            var oldImg = imgRect; var oldCrop = crop;
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            LayoutImage();
            if (oldImg.Width > 0 && !oldCrop.IsEmpty)
            {
                float k = imgRect.Width / (float)oldImg.Width;
                crop = new Rectangle(imgRect.X + (int)Math.Round((oldCrop.X - oldImg.X) * k), imgRect.Y + (int)Math.Round((oldCrop.Y - oldImg.Y) * k), (int)Math.Round(oldCrop.Width * k), (int)Math.Round(oldCrop.Height * k));
            }
        }

        /// <summary>틀 → 원본 그림 좌표.</summary>
        public Rectangle SourceCrop
        {
            get
            {
                float sc = Scale;
                int x = (int)Math.Round((crop.X - imgRect.X) / sc), y = (int)Math.Round((crop.Y - imgRect.Y) / sc);
                int side = (int)Math.Round(crop.Width / sc);
                side = Math.Clamp(side, 1, Math.Min(img.Width, img.Height));
                x = Math.Clamp(x, 0, img.Width - side); y = Math.Clamp(y, 0, img.Height - side);
                return new Rectangle(x, y, side, side);
            }
        }

        // ───────── 끌기 ─────────

        private Rectangle Corner(Drag d)
        {
            int h = S(9);
            Point c = d switch { Drag.NW => crop.Location, Drag.NE => new Point(crop.Right, crop.Top), Drag.SW => new Point(crop.Left, crop.Bottom), _ => new Point(crop.Right, crop.Bottom) };
            return new Rectangle(c.X - h, c.Y - h, h * 2, h * 2);
        }

        private Drag HitAt(Point pt)
        {
            foreach (var d in new[] { Drag.NW, Drag.NE, Drag.SW, Drag.SE }) if (Corner(d).Contains(pt)) return d;
            return crop.Contains(pt) ? Drag.Move : Drag.None;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            drag = HitAt(e.Location);
            grab = e.Location; grabCrop = crop;
            anchor = drag switch { Drag.NW => new Point(crop.Right, crop.Bottom), Drag.NE => new Point(crop.Left, crop.Bottom), Drag.SW => new Point(crop.Right, crop.Top), Drag.SE => crop.Location, _ => Point.Empty };
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = Drag.None; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag == Drag.None)
            {
                var h = HitAt(e.Location);
                Cursor = h == Drag.Move ? Cursors.SizeAll : h is Drag.NW or Drag.SE ? Cursors.SizeNWSE : h is Drag.NE or Drag.SW ? Cursors.SizeNESW : Cursors.Default;
                return;
            }
            if (drag == Drag.Move)
            {
                int x = Math.Clamp(grabCrop.X + e.X - grab.X, imgRect.Left, imgRect.Right - crop.Width);
                int y = Math.Clamp(grabCrop.Y + e.Y - grab.Y, imgRect.Top, imgRect.Bottom - crop.Height);
                crop = new Rectangle(x, y, crop.Width, crop.Height);
            }
            else
            {
                // 모서리를 끌면 맞은편 모서리를 붙잡아 두고 정사각형 그대로 키우고 줄인다
                int dx = drag is Drag.NE or Drag.SE ? 1 : -1, dy = drag is Drag.SW or Drag.SE ? 1 : -1;
                int want = Math.Max((e.X - anchor.X) * dx, (e.Y - anchor.Y) * dy);
                int maxW = dx > 0 ? imgRect.Right - anchor.X : anchor.X - imgRect.Left;
                int maxH = dy > 0 ? imgRect.Bottom - anchor.Y : anchor.Y - imgRect.Top;
                int side = Math.Clamp(want, MinSide, Math.Max(MinSide, Math.Min(maxW, maxH)));
                crop = new Rectangle(dx > 0 ? anchor.X : anchor.X - side, dy > 0 ? anchor.Y : anchor.Y - side, side, side);
            }
            Invalidate();
        }

        /// <summary>휠로 틀을 가운데 기준으로 키우고 줄인다.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = Math.Sign(e.Delta) * S(14);
            int maxSide = Math.Min(imgRect.Width, imgRect.Height);
            int side = Math.Clamp(crop.Width + step, MinSide, maxSide);
            int cx = crop.X + crop.Width / 2, cy = crop.Y + crop.Height / 2;
            int x = Math.Clamp(cx - side / 2, imgRect.Left, imgRect.Right - side), y = Math.Clamp(cy - side / 2, imgRect.Top, imgRect.Bottom - side);
            crop = new Rectangle(x, y, side, side);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, imgRect);
            // 틀 밖은 어둡게
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillRectangle(dim, imgRect.Left, imgRect.Top, imgRect.Width, crop.Top - imgRect.Top);
                g.FillRectangle(dim, imgRect.Left, crop.Bottom, imgRect.Width, imgRect.Bottom - crop.Bottom);
                g.FillRectangle(dim, imgRect.Left, crop.Top, crop.Left - imgRect.Left, crop.Height);
                g.FillRectangle(dim, crop.Right, crop.Top, imgRect.Right - crop.Right, crop.Height);
            }
            // 틀 테두리 + 3등분 선
            using (var line = new Pen(Color.FromArgb(150, 255, 255, 255), 1f))
            {
                for (int i = 1; i <= 2; i++)
                {
                    g.DrawLine(line, crop.Left + crop.Width * i / 3, crop.Top, crop.Left + crop.Width * i / 3, crop.Bottom);
                    g.DrawLine(line, crop.Left, crop.Top + crop.Height * i / 3, crop.Right, crop.Top + crop.Height * i / 3);
                }
            }
            using (var border = new Pen(Color.White, Math.Max(1.5f, DeviceDpi / 96f * 1.5f))) g.DrawRectangle(border, crop);
            // 모서리 손잡이
            using var hb = new SolidBrush(Color.White);
            foreach (var d in new[] { Drag.NW, Drag.NE, Drag.SW, Drag.SE })
            {
                var c = Corner(d);
                int s = S(5);
                g.FillRectangle(hb, c.X + c.Width / 2 - s, c.Y + c.Height / 2 - s, s * 2, s * 2);
            }
        }
    }
}
