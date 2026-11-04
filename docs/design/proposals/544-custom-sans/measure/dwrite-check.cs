// Proposal #544 (not approved): does DirectWrite pick a real face or simulate one?
// Run on Windows with the .NET 10 SDK, from this folder:
//   dotnet run dwrite-check.cs -- <font files...>
// For each weight and style the type ramps use, it prints the face that DirectWrite's own matcher
// (IDWriteFontFamily::GetFirstMatchingFont, the matcher under WinUI's text stack) returns from the
// given files, and whether that face carries DWRITE_FONT_SIMULATIONS_BOLD or _OBLIQUE.
// It calls DirectWrite's COM vtables by slot, in dwrite.h and dwrite_3.h order, so it needs no package.
#:property AllowUnsafeBlocks=true
using System;
using System.Linq;
using System.Runtime.InteropServices;

unsafe
{
    if (args.Length == 0) { Console.Error.WriteLine("usage: dotnet run dwrite-check.cs -- <font files>"); return 2; }
    var iidFactory3 = new Guid("9A1B41C3-D3BB-466A-87FC-FE67556A3B65");
    var iidBuilder1 = new Guid("3FF7715F-3CDC-4DC6-9B72-EC5621DCCAFD");
    void* factory;
    Check(DWriteCreateFactory(1 /* isolated */, &iidFactory3, &factory), "DWriteCreateFactory");
    void* builder0, builder;
    Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(factory, 36))(factory, &builder0), "CreateFontSetBuilder");
    Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(builder0, 0))(builder0, &iidBuilder1, &builder), "QueryInterface IDWriteFontSetBuilder1");
    Console.WriteLine("files: " + string.Join(", ", args.Select(System.IO.Path.GetFileName)));
    foreach (var path in args)
    {
        void* file;
        fixed (char* p = System.IO.Path.GetFullPath(path))
            Check(((delegate* unmanaged[Stdcall]<void*, char*, void*, void**, int>)Slot(factory, 7))(factory, p, null, &file), "CreateFontFileReference " + path);
        Check(((delegate* unmanaged[Stdcall]<void*, void*, int>)Slot(builder, 7))(builder, file), "AddFontFile " + path);
    }
    void* set, collection;
    Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(builder, 6))(builder, &set), "CreateFontSet");
    Check(((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Slot(factory, 37))(factory, set, &collection), "CreateFontCollectionFromFontSet");
    uint families = ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(collection, 3))(collection);
    int synthesized = 0;
    for (uint f = 0; f < families; f++)
    {
        void* family, famNames;
        Check(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(collection, 4))(collection, f, &family), "GetFontFamily");
        Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(family, 6))(family, &famNames), "GetFamilyNames");
        uint faces = ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(family, 4))(family);
        Console.WriteLine($"family \"{First(famNames)}\": {faces} faces");
        foreach (var (w, s) in new[] { (400, 0), (500, 0), (600, 0), (700, 0), (400, 2), (500, 2), (600, 2), (700, 2) })
        {
            void* font, faceNames;
            Check(((delegate* unmanaged[Stdcall]<void*, int, int, int, void**, int>)Slot(family, 7))(family, w, 5, s, &font), "GetFirstMatchingFont");
            int weight = ((delegate* unmanaged[Stdcall]<void*, int>)Slot(font, 4))(font);
            int style = ((delegate* unmanaged[Stdcall]<void*, int>)Slot(font, 6))(font);
            Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(font, 8))(font, &faceNames), "GetFaceNames");
            int sim = ((delegate* unmanaged[Stdcall]<void*, int>)Slot(font, 10))(font);
            if (sim != 0) synthesized++;
            string simText = sim == 0 ? "none (real face)"
                : string.Join("+", new[] { (sim & 1) != 0 ? "BOLD" : null, (sim & 2) != 0 ? "OBLIQUE" : null }.Where(x => x != null)) + " (SYNTHESIZED)";
            Console.WriteLine($"  asked {w} {(s == 2 ? "italic" : "upright"),-7} -> \"{First(faceNames)}\" {weight} {(style == 0 ? "upright" : style == 1 ? "oblique" : "italic")}; simulations: {simText}");
        }
    }
    Console.WriteLine($"synthesized matches: {synthesized}");
    return 0;

    static void* Slot(void* com, int i) => (*(void***)com)[i];
    static void Check(int hr, string what) { if (hr < 0) throw new Exception($"{what}: 0x{hr:X8}"); }
    static string First(void* strings)
    {
        uint len;
        Check(((delegate* unmanaged[Stdcall]<void*, uint, uint*, int>)Slot(strings, 7))(strings, 0, &len), "GetStringLength");
        var buf = new char[len + 1];
        fixed (char* b = buf) Check(((delegate* unmanaged[Stdcall]<void*, uint, char*, uint, int>)Slot(strings, 8))(strings, 0, b, len + 1), "GetString");
        return new string(buf, 0, (int)len);
    }
}

[DllImport("dwrite.dll")] static extern unsafe int DWriteCreateFactory(int type, Guid* iid, void** factory);
