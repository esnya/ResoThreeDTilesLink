namespace ThreeDTilesLink.Core.Models
{
    internal sealed record ResoniteDestinationPolicyOptions(
        string SessionDynamicSpaceName,
        string LicenseDynamicVariablePath,
        string AttributionRequirementsVariableLocalName,
        string AttributionRequirementsDynamicVariablePath,
        string PackageExportWarningSlotName,
        bool RequireCredit = true,
        bool CanExport = false,
        bool ApplyAvatarProtectionToSessionRoot = true,
        bool ApplyPackageExportProtectionToSessionRoot = true,
        bool ApplyAvatarProtectionToTileSlots = true,
        bool ApplyPackageExportProtectionToTileSlots = true)
    {
        internal static ResoniteDestinationPolicyOptions CreateDefault()
        {
            return new ResoniteDestinationPolicyOptions(
                SessionDynamicSpaceName: "ThreeDTilesLink",
                LicenseDynamicVariablePath: "World/ThreeDTilesLink.License",
                AttributionRequirementsVariableLocalName: "AttributionRequirements",
                AttributionRequirementsDynamicVariablePath: "World/ThreeDTilesLink.AttributionRequirements",
                PackageExportWarningSlotName: "ThreeDTilesLink Export Warning",
                RequireCredit: false,
                CanExport: true,
                ApplyAvatarProtectionToSessionRoot: false,
                ApplyPackageExportProtectionToSessionRoot: false,
                ApplyAvatarProtectionToTileSlots: false,
                ApplyPackageExportProtectionToTileSlots: false);
        }

        internal static ResoniteDestinationPolicyOptions CreateGoogleDefaults()
        {
            return new ResoniteDestinationPolicyOptions(
                SessionDynamicSpaceName: "Google3DTiles",
                LicenseDynamicVariablePath: "World/ThreeDTilesLink.License",
                AttributionRequirementsVariableLocalName: "AttributionRequirements",
                AttributionRequirementsDynamicVariablePath: "World/ThreeDTilesLink.AttributionRequirements",
                PackageExportWarningSlotName: "EXPORT PROHIBITED: STREAMED GOOGLE 3D TILES");
        }
    }
}
