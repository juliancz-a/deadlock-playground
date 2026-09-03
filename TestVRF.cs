using System;
using System.IO;
using System.Linq;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

public class TestVRF
{
    public static void Main()
    {
        var vpk = @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk";
        using var package = new Package();
        package.Read(vpk);
        var entry = package.FindEntry("models/heroes_staging/abrams/abrams.vmdl_c");
        package.ReadEntry(entry, out byte[] data);
        var res = new Resource();
        res.Read(new MemoryStream(data));
        
        var model = (Model)res.DataBlock;
        var loader = new ValveResourceFormat.IO.GameFileLoader(package, "models/heroes_staging/abrams/abrams.vmdl_c");
        
        var anims = model.GetAllAnimations(loader);
        foreach (var a in anims)
        {
            Console.WriteLine(a.Name);
        }
    }
}
