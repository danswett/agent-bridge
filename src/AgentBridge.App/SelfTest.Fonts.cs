using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace AgentBridge.App;

// No synthesized styles (ui.md §4.2, #544): every weight and style the ramps use is a real face
// of Slipgates Sans and Slipgates Mono, and Windows draws that face rather than faking one.
static partial class SelfTest
{
    static void CheckFonts(MainWindow window, Action<bool, string> check)
    {
        // DirectWrite's own matcher over each bundled collection, as the app ships it: for every
        // weight and style, the face it picks and whether it would simulate bold or oblique.
        var faked = new List<string>();
        var found = new List<string>();
        foreach (var (family, file) in Design.DesignKeys.FontCollections)
        {
            try
            {
                var matches = DirectWriteMatches(Path.Combine(AppContext.BaseDirectory, file), family);
                found.Add($"{family} {matches.Count}");
                foreach (var (weight, italic) in Design.DesignKeys.FontFaces)
                {
                    if (!matches.TryGetValue((weight, italic), out var m)) { faked.Add($"{family} {weight}{(italic ? " italic" : "")} not matched"); }
                    else if (m.Simulations != 0 || m.Weight != weight || m.Italic != italic) { faked.Add($"{family} {weight}{(italic ? " italic" : "")} -> {m.Weight}{(m.Italic ? " italic" : "")}, simulations {m.Simulations}"); }
                }
            }
            catch (Exception e) { faked.Add($"{family}: {e.Message}"); }
        }
        check(faked.Count == 0, $"has a real face, not a simulated one, for every weight and style in each bundled family ({string.Join(", ", found)}){(faked.Count > 0 ? $": {string.Join("; ", faked)}" : "")}");

        // XAML's own text in each weight and style is as wide as the real face's advances: a
        // faked bold or italic of another face, or a fallback to another font, isn't. Kerning is
        // off, as the advances were summed without it.
        var sans = ((FontFamily)Application.Current.Resources["SansFontFamily"]).Source.Split(',')[0].Trim();
        const double size = 200;
        var samples = Design.DesignKeys.SansProbeAdvances.Select(a =>
        {
            var block = new TextBlock
            {
                Text = Design.DesignKeys.SansProbe,
                FontFamily = new FontFamily(sans),
                FontSize = size,
                FontWeight = new FontWeight { Weight = (ushort)a.Weight },
                FontStyle = a.Italic ? FontStyle.Italic : FontStyle.Normal,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
                // Want is computed at the unscaled size, so Windows' text-size setting mustn't
                // enlarge the probe and fail a face that loaded correctly.
                IsTextScaleFactorEnabled = false,
            };
            Typography.SetKerning(block, false);
            return (a.Weight, a.Italic, Want: a.Advance * size / Design.DesignKeys.SansUnitsPerEm, Block: block);
        }).ToList();
        var host = new StackPanel { Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var s in samples) { host.Children.Add(s.Block); }
        var root = (Panel)window.RootElement;
        root.Children.Add(host);
        try
        {
            host.UpdateLayout();
            var off = samples.Where(s => Math.Abs(s.Block.ActualWidth - s.Want) > Math.Max(2, s.Want * 0.002))
                .Select(s => $"{s.Weight}{(s.Italic ? " italic" : "")} {s.Block.ActualWidth:0.#} px, {s.Want:0.#} wanted").ToList();
            check(off.Count == 0, $"draws Slipgates Sans' real face for every weight and style, as wide as its advances ({sans}){(off.Count > 0 ? $": {string.Join("; ", off)}" : "")}");
        }
        finally { root.Children.Remove(host); }
    }

    // DirectWrite's matcher (IDWriteFontFamily::GetFirstMatchingFont, under WinUI's text stack)
    // over one font file, for one family: the face it returns for each weight and style, and its
    // DWRITE_FONT_SIMULATIONS. Its COM vtables are called by slot, in dwrite.h and dwrite_3.h order.
    static unsafe Dictionary<(int Weight, bool Italic), (int Weight, bool Italic, int Simulations)> DirectWriteMatches(string path, string familyName)
    {
        var iidFactory3 = new Guid("9A1B41C3-D3BB-466A-87FC-FE67556A3B65");
        var iidBuilder1 = new Guid("3FF7715F-3CDC-4DC6-9B72-EC5621DCCAFD");
        var held = new List<nint>();
        void* Hold(void* com) { held.Add((nint)com); return com; }
        try
        {
            void* factory, builder0, builder, file, set, collection;
            Hr(DWriteCreateFactory(1 /* isolated */, &iidFactory3, &factory), "DWriteCreateFactory");
            Hold(factory);
            Hr(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(factory, 36))(factory, &builder0), "CreateFontSetBuilder");
            Hold(builder0);
            Hr(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(builder0, 0))(builder0, &iidBuilder1, &builder), "IDWriteFontSetBuilder1");
            Hold(builder);
            fixed (char* p = path) { Hr(((delegate* unmanaged[Stdcall]<void*, char*, void*, void**, int>)Slot(factory, 7))(factory, p, null, &file), "CreateFontFileReference"); }
            Hold(file);
            Hr(((delegate* unmanaged[Stdcall]<void*, void*, int>)Slot(builder, 7))(builder, file), "AddFontFile");
            Hr(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(builder, 6))(builder, &set), "CreateFontSet");
            Hold(set);
            Hr(((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Slot(factory, 37))(factory, set, &collection), "CreateFontCollectionFromFontSet");
            Hold(collection);
            uint index, exists;
            fixed (char* n = familyName) { Hr(((delegate* unmanaged[Stdcall]<void*, char*, uint*, uint*, int>)Slot(collection, 5))(collection, n, &index, &exists), "FindFamilyName"); }
            if (exists == 0) { throw new InvalidOperationException($"{Path.GetFileName(path)} has no family \"{familyName}\""); }
            void* family;
            Hr(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(collection, 4))(collection, index, &family), "GetFontFamily");
            Hold(family);
            var matches = new Dictionary<(int, bool), (int, bool, int)>();
            foreach (var (weight, italic) in Design.DesignKeys.FontFaces)
            {
                void* font;
                Hr(((delegate* unmanaged[Stdcall]<void*, int, int, int, void**, int>)Slot(family, 7))(family, weight, 5 /* normal stretch */, italic ? 2 : 0, &font), "GetFirstMatchingFont");
                Hold(font);
                var got = ((delegate* unmanaged[Stdcall]<void*, int>)Slot(font, 4))(font);
                var style = ((delegate* unmanaged[Stdcall]<void*, int>)Slot(font, 6))(font);
                var simulations = ((delegate* unmanaged[Stdcall]<void*, int>)Slot(font, 10))(font);
                matches[(weight, italic)] = (got, style != 0, simulations);
            }
            return matches;
        }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--) { ((delegate* unmanaged[Stdcall]<void*, uint>)Slot((void*)held[i], 2))((void*)held[i]); }
        }

        static void* Slot(void* com, int i) => (*(void***)com)[i];
        static void Hr(int hr, string what) { if (hr < 0) { throw new COMException(what, hr); } }
    }

    [DllImport("dwrite.dll")]
    static extern unsafe int DWriteCreateFactory(int type, Guid* iid, void** factory);
}
