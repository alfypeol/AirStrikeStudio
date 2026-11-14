using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
namespace AirstrikeStudio {
public static class AssemblyTests {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 public static void Run(string data,string output){
  Directory.CreateDirectory(output);
  using(var assets=new Assets()){
   assets.Open(data);
   string[] names={"helic3_big_black","rocketlauncher_mobile_lgreen","boss2","tank_small_green"};int[] counts={3,5,6,3};
   for(int i=0;i<names.Length;i++)using(var v=new ModelViewer(assets,names[i])){
    Check(v.Assembly.PartCount==counts[i],names[i]+" part count: "+v.Assembly.PartCount);
    if(i==0){var pos=v.Assembly.Positions.Single(x=>x.Key.Contains("vint_a3_red_i")).Value;Check(Math.Abs(pos.Z-12.3982906)<.0001,"Helicopter rotor tag placement");}
    if(i==1){var pos=v.Assembly.Positions.Single(x=>x.Key.Contains("rocketblock")).Value;Check(Math.Abs(pos.X-.72024000674719)<.000001&&Math.Abs(pos.Y+42.2888431549072)<.000001&&Math.Abs(pos.Z-16.1757006645203)<.000001,"Nested rack accumulates both tag translations exactly");}
    v.Snapshot(Path.Combine(output,names[i]+".png"));v.Orbit(95,20);v.Snapshot(Path.Combine(output,names[i]+"-rotated.png"));v.SetAttachments(false);v.Snapshot(Path.Combine(output,names[i]+"-base.png"));v.SetAttachments(true);
    File.WriteAllLines(Path.Combine(output,names[i]+"-parts.txt"),v.Assembly.Report);
    Console.WriteLine("PASS "+names[i]+": "+v.Assembly.PartCount+" mesh parts, orbit and attachment toggle");
   }
   var warnings=new List<string>();var parsed=Attachment.Parse("attach id \"LEFT\" abs night \"piece\" \"tag\"\nattach \"other\" origin\nattach abs",warnings);
   Check(parsed.Count==2&&parsed[0].Id=="LEFT"&&parsed[0].Absolute&&parsed[0].Night&&parsed[1].Tag=="origin"&&warnings.Count==1,"Attachment option parsing");
   Definition template=assets.Definitions[names[0]];
   var synthetic=new Definition{Name="test_cycle",Model=template.Model,Skin=template.Skin};
   synthetic.Attachments.Add(new Attachment{ObjectName="test_cycle",Tag="origin"});
   synthetic.Attachments.Add(new Attachment{ObjectName="missing_test_object",Tag="origin"});
   synthetic.Attachments.Add(new Attachment{ObjectName=names[0],Tag="missing_test_tag"});
   assets.Definitions.Add(synthetic.Name,synthetic);var scene=AssemblyScene.Load(assets,synthetic.Name);
   Check(scene.PartCount==1&&scene.Report.Any(x=>x.Contains("cycle"))&&scene.Report.Any(x=>x.Contains("missing definition"))&&scene.Report.Any(x=>x.Contains("missing or unsupported tag")),"Graceful missing/cyclic attachment handling");assets.Definitions.Remove(synthetic.Name);
   // Validate all visible base-mesh definitions against the same assembly code.
   int loaded=0,failures=0,parts=0;var log=new List<string>();
   foreach(var d in assets.Definitions.Values.Where(x=>x.Model.Length>0&&x.Skin.Length>0&&!x.Hidden&&!x.Additive)){
    try{var a=AssemblyScene.Load(assets,d.Name);loaded++;parts+=a.PartCount;log.Add(d.Name+": "+a.PartCount+" mesh parts");foreach(string msg in a.Report.Where(x=>!x.StartsWith("Loaded ")))log.Add("  "+msg);}
    catch(Exception ex){failures++;log.Add("FAILED "+d.Name+": "+ex.Message);}
   }
   File.WriteAllLines(Path.Combine(output,"asset-coverage.txt"),log);
   Console.WriteLine("PASS parser variants and missing/cyclic attachment guards");Console.WriteLine("COVERAGE "+loaded+" visible definitions loaded, "+parts+" mesh instances, "+failures+" failed base models");
  }
 }
}
}
