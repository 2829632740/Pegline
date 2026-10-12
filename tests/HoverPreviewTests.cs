using System;
using System.Windows;
using Drawing = System.Drawing;

namespace Pegline
{
    static class HoverPreviewTests
    {
        public static void Run()
        {
            Check("wide preview stays below its card", delegate
            {
                var anchor = new Rect(880, 60, 160, 100);
                var area = new Rect(0, 0, 1920, 1040);
                var bounds = PreviewLayout.Bounds(new Size(4000, 2000), anchor, area, 1);
                Contained(bounds, area);
                Assert(bounds.Top >= 172, "preview overlaps the hovered card");
                Assert(bounds.Width <= 900 && bounds.Height <= 650, "preview exceeds its size limit");
                Aspect(bounds, 2, 1);
            });
            Check("negative monitor origins remain in physical pixels", delegate
            {
                var area = new Rect(-1920, -200, 1920, 1080);
                var anchor = new Rect(-1910, -140, 150, 110);
                var bounds = PreviewLayout.Bounds(new Size(3200, 1800), anchor, area, 1.5);
                Contained(bounds, area);
                Assert(bounds.Left < 0, "preview jumped to the primary monitor");
                Assert(bounds.Top >= -12, "preview overlaps the card on the negative-origin monitor");
                Aspect(bounds, 16.0 / 9, 1.5);
            });
            Check("tall images fit the space below the rope", delegate
            {
                var area = new Rect(0, 0, 1280, 720);
                var bounds = PreviewLayout.Bounds(new Size(1000, 6000), new Rect(500, 40, 150, 110), area, 1);
                Contained(bounds, area);
                Assert(bounds.Top >= 162 && bounds.Bottom <= 708, "tall preview extends outside available height");
                Aspect(bounds, 1.0 / 6, 1);
            });
            Check("high DPI previews use the target monitor scale", delegate
            {
                var area = new Rect(0, 0, 3840, 2160);
                var bounds = PreviewLayout.Bounds(new Size(10000, 5000), new Rect(1900, 100, 300, 200), area, 2);
                Contained(bounds, area);
                Assert(bounds.Width > 900 && bounds.Width <= 1800, "high DPI cap was treated as unscaled pixels");
                Assert(bounds.Height <= 1300 && bounds.Top >= 324, "high DPI placement exceeds available bounds");
                Aspect(bounds, 2, 2);
            });
            Check("preview uses space above when no space remains below", delegate
            {
                var area = new Rect(0, 0, 1280, 720);
                var bounds = PreviewLayout.Bounds(new Size(1600, 1000), new Rect(500, 680, 150, 35), area, 1);
                Contained(bounds, area);
                Assert(bounds.Bottom <= 668, "preview overlaps card despite available room above");
            });
            Check("small source images are not enlarged into blurry previews", delegate
            {
                var bounds = PreviewLayout.Bounds(new Size(40, 20), new Rect(300, 30, 100, 80), new Rect(0, 0, 1280, 720), 1);
                Assert(bounds.Width <= 52 && bounds.Height <= 32, "small source was enlarged");
                Aspect(bounds, 2, 1);
            });
            Check("invalid dimensions suppress the preview", delegate
            {
                var source = new Size(800, 600);
                var anchor = new Rect(100, 50, 100, 100);
                var area = new Rect(0, 0, 1280, 720);
                Assert(PreviewLayout.Bounds(new Size(0, 10), anchor, area, 1).IsEmpty, "zero-width source was accepted");
                Assert(PreviewLayout.Bounds(source, Rect.Empty, area, 1).IsEmpty, "missing anchor was accepted");
                Assert(PreviewLayout.Bounds(source, anchor, Rect.Empty, 1).IsEmpty, "empty work area was accepted");
                Assert(PreviewLayout.Bounds(source, anchor, area, double.NaN).IsEmpty, "NaN scale was accepted");
                Assert(PreviewLayout.Bounds(source, anchor, area, 0).IsEmpty, "zero scale was accepted");
            });
            Check("hover waits before requesting one image", delegate
            {
                var state = new PreviewHoverState();
                var id = Guid.NewGuid();
                var revision = new object();
                var now = new DateTime(2026, 10, 12);
                Assert(!state.Observe(id, revision, now, now), "first hover requested an image immediately");
                Assert(!state.Observe(id, revision, now, now.AddMilliseconds(349)), "brief hover requested an image");
                Assert(state.Observe(id, revision, now, now.AddMilliseconds(350)), "stable hover never requested its image");
                Assert(!state.Observe(id, revision, now, now.AddMilliseconds(700)), "stable hover started duplicate work");
                Assert(state.IsCurrent(state.Version), "current image request was invalidated");
            });
            Check("switching cards cancels the previous request and restarts dwell", delegate
            {
                var state = new PreviewHoverState();
                var now = new DateTime(2026, 10, 12);
                var first = Guid.NewGuid();
                var second = Guid.NewGuid();
                var revision = new object();
                state.Observe(first, revision, now, now);
                state.Observe(first, revision, now, now.AddMilliseconds(350));
                int stale = state.Version;
                Assert(!state.Observe(second, revision, now, now.AddMilliseconds(360)), "card switch skipped dwell");
                Assert(!state.IsCurrent(stale), "previous image can reappear after a card switch");
                Assert(!state.Observe(second, revision, now, now.AddMilliseconds(709)), "second card requested too soon");
                Assert(state.Observe(second, revision, now, now.AddMilliseconds(710)), "second card never became ready");
            });
            Check("leaving cancels in-flight work and requires a fresh hover", delegate
            {
                var state = new PreviewHoverState();
                var now = new DateTime(2026, 10, 12);
                var id = Guid.NewGuid();
                var revision = new object();
                state.Observe(id, revision, now, now);
                state.Observe(id, revision, now, now.AddMilliseconds(350));
                int stale = state.Version;
                state.Reset();
                Assert(!state.IsCurrent(stale), "hidden preview accepts an old image result");
                Assert(!state.Observe(id, revision, now, now.AddMilliseconds(500)), "returning to the card skipped dwell");
                Assert(!state.Observe(id, revision, now, now.AddMilliseconds(849)), "returning hover became ready too soon");
                Assert(state.Observe(id, revision, now, now.AddMilliseconds(850)), "returning hover never became ready");
            });
            Check("edited images replace the pending revision", delegate
            {
                var state = new PreviewHoverState();
                var now = new DateTime(2026, 10, 12);
                var id = Guid.NewGuid();
                var revision = new object();
                state.Observe(id, revision, now, now);
                state.Observe(id, revision, now, now.AddMilliseconds(350));
                int stale = state.Version;
                var edited = new object();
                Assert(state.Observe(id, edited, now, now.AddMilliseconds(400)), "edited thumbnail did not refresh");
                Assert(!state.IsCurrent(stale), "old thumbnail result can overwrite the edited image");
                stale = state.Version;
                Assert(state.Observe(id, edited, now.AddSeconds(1), now.AddMilliseconds(450)), "file revision did not refresh");
                Assert(!state.IsCurrent(stale), "old file revision is still current");
            });
        }

        static void Contained(Drawing.Rectangle bounds, Rect area)
        {
            Assert(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0, "preview has no usable area");
            Assert(bounds.Left >= area.Left && bounds.Top >= area.Top && bounds.Right <= area.Right && bounds.Bottom <= area.Bottom,
                "preview escaped the monitor work area");
        }

        static void Aspect(Drawing.Rectangle bounds, double expected, double scale)
        {
            double ratio = (bounds.Width - 12 * scale) / (bounds.Height - 12 * scale);
            Assert(Math.Abs(ratio - expected) <= Math.Max(0.015, expected * 0.015), "preview changed the image aspect ratio");
        }

        static void Check(string name, Action test)
        {
            test();
            Console.WriteLine("PASS " + name);
        }

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
