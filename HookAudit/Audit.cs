using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace IdleSpiralSpeedPatch
{
    public static class Loader
    {
        public static void Install()
        {
            var lines=Harmony.GetAllPatchedMethods().SelectMany(method=>Harmony.GetPatchInfo(method).Owners
                .Select(owner=>owner+" | "+method.DeclaringType.FullName+"."+method.Name)).OrderBy(line=>line).ToArray();
            var observers=lines.Where(line=>line.Contains("observer") || line.Contains(".formula")).ToArray();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"hook-audit.txt"),
                "temporaryObserversRemaining="+observers.Length+"\nauditAddsHooks=false\n"+string.Join("\n",lines)+"\n");
            if(observers.Length!=0)throw new InvalidOperationException("Temporary observer hooks remain.");
        }
    }
}
