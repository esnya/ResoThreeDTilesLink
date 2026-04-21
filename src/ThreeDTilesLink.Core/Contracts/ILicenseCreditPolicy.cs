namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ILicenseCreditPolicy
    {
        string DefaultCredit { get; }

        string AttributionRequirements { get; }

        string? NormalizeOwner(string? value);
    }
}
