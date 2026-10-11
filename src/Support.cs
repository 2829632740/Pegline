using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Media;
using Microsoft.Win32;

namespace Pegline
{
    /// Tiny three-language helper. The app has a handful of strings, so a
    /// resource file setup would be more ceremony than content.
    static class Loc
    {
        static readonly string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        public static string L(string english, string spanish, string chinese)
        {
            if (lang == "zh") return chinese;
            if (lang == "es") return spanish;
            return english;
        }
    }

    /// Set PEGLINE_TRACE=1, or create %APPDATA%\Pegline	race.on, to log gestures to %APPDATA%\Pegline	race.log.
    static class Trace
    {
        static readonly bool on = Environment.GetEnvironmentVariable("PEGLINE_TRACE") == "1"
                                  || File.Exists(Path.Combine(Settings.Dir, "trace.on"));

        public static void Write(string message)
        {
            if (!on) return;
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Path.Combine(Settings.Dir, "trace.log"),
                                   DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    /// Settings live in a plain key=value file under %APPDATA%\Pegline.
    static class Settings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pegline");
        static readonly string file = Path.Combine(Dir, "settings.ini");
        static readonly string peggedFile = Path.Combine(Dir, "pegged.txt");
        static Dictionary<string, string> values;

        static Dictionary<string, string> Values
        {
            get
            {
                if (values != null) return values;
                values = new Dictionary<string, string>();
                try
                {
                    if (File.Exists(file))
                        foreach (var line in File.ReadAllLines(file))
                        {
                            int i = line.IndexOf('=');
                            if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                        }
                }
                catch (IOException) { }
                return values;
            }
        }

        public static bool Get(string key)
        {
            string v;
            return Values.TryGetValue(key, out v) && v == "1";
        }

        public static void Set(string key, bool on)
        {
            Values[key] = on ? "1" : "0";
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                foreach (var kv in Values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(file, lines);
            }
            catch (IOException) { }
        }

        public static string[] Pegged
        {
            get
            {
                try { return File.Exists(peggedFile) ? File.ReadAllLines(peggedFile) : new string[0]; }
                catch (IOException) { return new string[0]; }
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllLines(peggedFile, value);
                }
                catch (IOException) { }
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool OpensAtLogin
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue("Pegline") != null;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue("Pegline", "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"");
                    else key.DeleteValue("Pegline", false);
                }
            }
        }
    }

    /// The three small sounds, synthesized once in memory: a light tink when
    /// a photo hangs, a soft pop when it leaves, and a paper rustle for the
    /// Recycle Bin. No files to ship, no volume surprises.
    static class Sounds
    {
        static byte[] tink, pop, rustle;

        public static void Tink() { Play(ref tink, 0); }
        public static void Pop() { Play(ref pop, 1); }
        public static void Rustle() { Play(ref rustle, 2); }

        static void Play(ref byte[] cache, int kind)
        {
            try
            {
                if (cache == null) cache = Make(kind);
                new SoundPlayer(new MemoryStream(cache)).Play();
            }
            catch (Exception) { }
        }

        static byte[] Make(int kind)
        {
            const int rate = 44100;
            double seconds = kind == 0 ? 0.18 : kind == 1 ? 0.09 : 0.28;
            int n = (int)(rate * seconds);
            var samples = new short[n];
            var rnd = new Random(7);
            double phase = 0, low = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, v;
                if (kind == 0)
                {
                    double env = Math.Exp(-t * 32);
                    v = 0.35 * env * (Math.Sin(2 * Math.PI * 1760 * t) * 0.7 + Math.Sin(2 * Math.PI * 3520 * t) * 0.3);
                }
                else if (kind == 1)
                {
                    double f = 420 - 2400 * t;
                    phase += 2 * Math.PI * f / rate;
                    double env = Math.Sin(Math.PI * Math.Min(1, t / seconds)) * Math.Exp(-t * 18);
                    v = 0.28 * env * Math.Sin(phase);
                }
                else
                {
                    low += (rnd.NextDouble() * 2 - 1 - low) * 0.35;
                    double env = Math.Sin(Math.PI * t / seconds) * (0.6 + 0.4 * Math.Sin(t * 90));
                    v = 0.22 * env * low;
                }
                samples[i] = (short)(Math.Max(-1, Math.Min(1, v)) * short.MaxValue);
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
            w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
            foreach (var s in samples) w.Write(s);
            return ms.ToArray();
        }
    }

    /// A damped spring, integrated by hand every frame. The Mac version gets
    /// these from SwiftUI; here each value that moves owns one.
    class Spring
    {
        public double Value, Velocity, Target;
        double k, c;

        /// SwiftUI's spring(response:dampingFraction:).
        public Spring(double response, double damping, double value)
        {
            SetResponse(response, damping);
            Value = Target = value;
        }

        public void SetResponse(double response, double damping)
        {
            k = Math.Pow(2 * Math.PI / response, 2);
            c = 4 * Math.PI * damping / response;
        }

        /// SwiftUI's interpolatingSpring(stiffness:damping:), unit mass.
        public void SetStiffness(double stiffness, double damping)
        {
            k = stiffness;
            c = damping;
        }

        public void Step(double dt)
        {
            // Small sub-steps keep stiff springs stable at any frame rate.
            int steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
            double h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                double a = -k * (Value - Target) - c * Velocity;
                Velocity += a * h;
                Value += Velocity * h;
            }
        }

        public bool Settled
        {
            get { return Math.Abs(Value - Target) < 0.001 && Math.Abs(Velocity) < 0.001; }
        }
    }

    static class Ease
    {
        public static double InOutCubic(double x)
        {
            return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
        }

        public static double Smooth(double x, double a, double b)
        {
            double t = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        public static double OutQuad(double x) { return 1 - (1 - x) * (1 - x); }
    }
}
