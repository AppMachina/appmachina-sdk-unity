#if UNITY_IOS
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace AppMachina.Unity.Editor
{
    /// <summary>
    /// iOS post-build processor for the AppMachina SDK.
    /// Modifies the Xcode project after Unity generates it:
    ///   - Info.plist: ATT usage description, SKAdNetwork IDs, URL schemes
    ///   - Xcode project: required frameworks, bitcode disabled, entitlements
    ///
    /// Reads configuration from a <see cref="AppMachinaSettings"/> ScriptableObject
    /// in any Resources folder. If no settings asset exists, the processor is skipped.
    /// </summary>
    public class AppMachinaPostBuildProcessor : IPostprocessBuildWithReport
    {
        /// <summary>
        /// Callback order. Runs after Unity's own post-processing (order 0)
        /// but early enough for other plugins to layer on top.
        /// </summary>
        public int callbackOrder => 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS)
                return;

            var settings = AppMachinaSettings.Instance;
            if (settings == null)
            {
                Debug.Log("[AppMachina] No AppMachinaSettings asset found in Resources. " +
                          "Skipping iOS post-build processing. " +
                          "Create one via Assets > Create > AppMachina > Settings.");
                return;
            }

            string buildPath = report.summary.outputPath;
            string plistPath = Path.Combine(buildPath, "Info.plist");

            ModifyInfoPlist(plistPath, settings);
            ModifyXcodeProject(buildPath, settings);

            Debug.Log("[AppMachina] iOS post-build processing complete.");
        }

        // ── Info.plist ──────────────────────────────────────────────────

        private static void ModifyInfoPlist(string plistPath, AppMachinaSettings settings)
        {
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            var root = plist.root;

            // 1. ATT usage description
            if (!string.IsNullOrEmpty(settings.attUsageDescription))
            {
                root.SetString("NSUserTrackingUsageDescription",
                    settings.attUsageDescription);
            }

            // 2. SKAdNetwork IDs (idempotent — deduplicates against existing entries)
            AddSKAdNetworkIds(root, settings);

            // 3. URL schemes for deep linking (idempotent — merges into existing entry)
            AddUrlSchemes(root, settings);

            plist.WriteToFile(plistPath);
        }

        /// <summary>
        /// Add SKAdNetwork identifiers to Info.plist. Merges with any IDs that
        /// other plugins (e.g., ad network SDKs) may have already added.
        /// </summary>
        private static void AddSKAdNetworkIds(PlistElementDict root, AppMachinaSettings settings)
        {
            // Collect all IDs we want to add
            var idsToAdd = new HashSet<string>();

            if (settings.includeDefaultSKAdNetworkIds)
            {
                foreach (string id in AppMachinaSettings.DefaultSKAdNetworkIds)
                    idsToAdd.Add(id);
            }

            if (settings.additionalSKAdNetworkIds != null)
            {
                foreach (string id in settings.additionalSKAdNetworkIds)
                {
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    if (!id.EndsWith(".skadnetwork"))
                    {
                        Debug.LogWarning(
                            $"[AppMachina] Invalid SKAdNetwork ID \"{id}\" — " +
                            "must end with .skadnetwork. Skipping.");
                        continue;
                    }

                    idsToAdd.Add(id);
                }
            }

            if (idsToAdd.Count == 0)
                return;

            // Read existing IDs so we don't add duplicates
            PlistElementArray skanArray;
            var existingElement = root["SKAdNetworkItems"];

            if (existingElement != null && existingElement is PlistElementArray existing)
            {
                skanArray = existing;

                // Remove IDs already present from our set
                foreach (var item in skanArray.values)
                {
                    if (item is PlistElementDict dict)
                    {
                        var existingId = dict["SKAdNetworkIdentifier"];
                        if (existingId != null)
                            idsToAdd.Remove(existingId.AsString());
                    }
                }
            }
            else
            {
                skanArray = root.CreateArray("SKAdNetworkItems");
            }

            // Append new IDs
            foreach (string id in idsToAdd)
            {
                var dict = skanArray.AddDict();
                dict.SetString("SKAdNetworkIdentifier", id);
            }
        }

        /// <summary>
        /// Add URL schemes for deep linking. Uses "AppMachinaDeepLinks" as the
        /// CFBundleURLName so repeated builds don't duplicate entries.
        /// </summary>
        private static void AddUrlSchemes(PlistElementDict root, AppMachinaSettings settings)
        {
            if (settings.urlSchemes == null || settings.urlSchemes.Length == 0)
                return;

            PlistElementArray urlTypes;
            var existingElement = root["CFBundleURLTypes"];

            if (existingElement != null && existingElement is PlistElementArray existing)
            {
                urlTypes = existing;
            }
            else
            {
                urlTypes = root.CreateArray("CFBundleURLTypes");
            }

            // Look for an existing AppMachina entry to merge into
            PlistElementDict appMachinaEntry = null;
            PlistElementArray appMachinaSchemes = null;
            var existingSchemes = new HashSet<string>();

            foreach (var item in urlTypes.values)
            {
                if (item is PlistElementDict dict)
                {
                    var nameElement = dict["CFBundleURLName"];
                    if (nameElement != null && nameElement.AsString() == "AppMachinaDeepLinks")
                    {
                        appMachinaEntry = dict;
                        var schemesElement = dict["CFBundleURLSchemes"];
                        if (schemesElement is PlistElementArray arr)
                        {
                            appMachinaSchemes = arr;
                            foreach (var s in arr.values)
                                existingSchemes.Add(s.AsString());
                        }
                        break;
                    }
                }
            }

            if (appMachinaEntry == null)
            {
                appMachinaEntry = urlTypes.AddDict();
                appMachinaEntry.SetString("CFBundleURLName", "AppMachinaDeepLinks");
                appMachinaSchemes = appMachinaEntry.CreateArray("CFBundleURLSchemes");
            }
            else if (appMachinaSchemes == null)
            {
                appMachinaSchemes = appMachinaEntry.CreateArray("CFBundleURLSchemes");
            }

            foreach (string scheme in settings.urlSchemes)
            {
                if (!string.IsNullOrWhiteSpace(scheme) && !existingSchemes.Contains(scheme))
                    appMachinaSchemes.AddString(scheme);
            }
        }

        // ── Xcode Project ───────────────────────────────────────────────

        private static void ModifyXcodeProject(string buildPath, AppMachinaSettings settings)
        {
            string projPath = PBXProject.GetPBXProjectPath(buildPath);
            var project = new PBXProject();
            project.ReadFromFile(projPath);

            string mainTarget = project.GetUnityMainTargetGuid();
            string frameworkTarget = project.GetUnityFrameworkTargetGuid();

            // Required frameworks (weak-linked so the app still runs on older iOS)
            project.AddFrameworkToProject(mainTarget,
                "AppTrackingTransparency.framework", true);
            project.AddFrameworkToProject(mainTarget,
                "AdSupport.framework", true);
            project.AddFrameworkToProject(mainTarget,
                "AdServices.framework", true);
            project.AddFrameworkToProject(mainTarget,
                "StoreKit.framework", true);

            // Disable bitcode — required for Rust static libraries
            project.SetBuildProperty(mainTarget, "ENABLE_BITCODE", "NO");
            project.SetBuildProperty(frameworkTarget, "ENABLE_BITCODE", "NO");

            // Associated domains entitlement for Universal Links
            AddAssociatedDomainsEntitlement(buildPath, project, mainTarget, settings);

            project.WriteToFile(projPath);
        }

        /// <summary>
        /// Create or update an entitlements file with associated-domains entries
        /// for Universal Links. If the project already has an entitlements file,
        /// we read it and merge; otherwise we create a new one.
        /// </summary>
        private static void AddAssociatedDomainsEntitlement(
            string buildPath,
            PBXProject project,
            string mainTarget,
            AppMachinaSettings settings)
        {
            if (settings.associatedDomains == null || settings.associatedDomains.Length == 0)
                return;

            // Check if the project already references an entitlements file
            string existingEntitlements = project.GetBuildPropertyForAnyConfig(
                mainTarget, "CODE_SIGN_ENTITLEMENTS");

            string entitlementsRelPath;
            string entitlementsAbsPath;

            if (!string.IsNullOrEmpty(existingEntitlements))
            {
                entitlementsRelPath = existingEntitlements;
                entitlementsAbsPath = Path.Combine(buildPath, existingEntitlements);
            }
            else
            {
                // Unity's default target folder name
                entitlementsRelPath = "Unity-iPhone/AppMachina.entitlements";
                entitlementsAbsPath = Path.Combine(buildPath, entitlementsRelPath);
            }

            // Read existing entitlements or create new
            var entitlements = new PlistDocument();
            if (File.Exists(entitlementsAbsPath))
            {
                entitlements.ReadFromFile(entitlementsAbsPath);
            }

            // Merge domains
            const string domainsKey = "com.apple.developer.associated-domains";
            var existingDomains = new HashSet<string>();

            PlistElementArray domainsArray;
            var existingElement = entitlements.root[domainsKey];

            if (existingElement != null && existingElement is PlistElementArray existing)
            {
                domainsArray = existing;
                foreach (var item in domainsArray.values)
                    existingDomains.Add(item.AsString());
            }
            else
            {
                domainsArray = entitlements.root.CreateArray(domainsKey);
            }

            foreach (string domain in settings.associatedDomains)
            {
                if (string.IsNullOrWhiteSpace(domain))
                    continue;

                string formatted = domain.StartsWith("applinks:")
                    ? domain
                    : $"applinks:{domain}";

                if (!existingDomains.Contains(formatted))
                    domainsArray.AddString(formatted);
            }

            // Ensure directory exists
            string dir = Path.GetDirectoryName(entitlementsAbsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            entitlements.WriteToFile(entitlementsAbsPath);

            // Add the file to the Xcode project and set the build property
            if (string.IsNullOrEmpty(existingEntitlements))
            {
                project.AddFile(entitlementsAbsPath, entitlementsRelPath);
                project.SetBuildProperty(mainTarget,
                    "CODE_SIGN_ENTITLEMENTS", entitlementsRelPath);
            }
        }
    }
}
#endif
