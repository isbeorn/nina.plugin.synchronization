using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Synchronization")]
[assembly: AssemblyDescription("A plugin that introduces synchronization instructions for dithering")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Stefan Berg")]
[assembly: AssemblyProduct("NINA.Plugins")]
[assembly: AssemblyCopyright("Copyright ©  2021")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: InternalsVisibleTo("NINA.Plugins.Test")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("c60db3b2-9a08-4b24-8938-bfb01e770eb9")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("0.2.1.1")]
[assembly: AssemblyFileVersion("0.2.1.1")]

//The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "2.0.0.2001")]

//Your plugin homepage - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://www.patreon.com/stefanberg/")]
//The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
//The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
//The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://bitbucket.org/Isbeorn/nina.plugin.synchronization/")]

[assembly: AssemblyMetadata("ChangelogURL", "https://bitbucket.org/Isbeorn/nina.plugin.synchronization/src/master/Synchronization/Changelog.md")]

//Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Dither,Synchronization,Multiple Cameras")]

//The featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://bitbucket.org/Isbeorn/nina.plugin.synchronization/downloads/SynchronizationLogo.jpg")]
//An example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://bitbucket.org/Isbeorn/nina.plugin.synchronization/downloads/SynchronizationSample.jpg")]
//An additional example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"This plugin is intended for people that want to dither on a setup with multiple cameras on one single mount. 
For that it has to be ensured that all imaging instances will sync up on each other before commencing a dither operation.  
  
*Note:* The plugin needs robust testing in live conditions. Feedback for failure scenarios are appreciated in the #plugin-discusissions N.I.N.A. discord channel. Thanks!

*Prerequisites*:
* At least one instance of N.I.N.A. needs to be connected to the guider
* Only one instance should handle and be connected to the mount

*Usage*:
* The first instance of N.I.N.A. that starts will register a server that orchestrates the dither workflow. This instance must remain active for the complete duration of your imaging acquisition.  
* To make use of the synchronized dithering a new instruction trigger is available for advanced sequences  
* Simply replace your normal dither trigger with the 'Synchronized Dither' trigger  
* Each trigger will register itself against the server when the instruction set where the trigger is placed in is active and unregister itself automatically when the instruction set is left  
* Make sure that your **exposure time * dither after exposures** roughly matches for each instance, as every time the trigger is fired the trigger will wait for all instances to be synced up  
* When the triggers are synced up, one instance will be picked as the leader, among those that are connected to a guider, that will run the dither command. The others will wait for the dither to finish and then continue with the sequence  

")]
