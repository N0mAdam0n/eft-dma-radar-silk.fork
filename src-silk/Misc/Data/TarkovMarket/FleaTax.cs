// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc.Data.TarkovMarket
{
    internal static class FleaTax
    {
        private const double CommunityItemTax = 3d;
        private const double CommunityRequirementTax = 3d;
        private const double RagFairCommissionModifier = 1d;

        /// <summary>
        /// Calculates the flea market tax for a listing.
        /// </summary>
        /// <param name="requirementsPrice">Desired flea list price.</param>
        /// <param name="basePrice">Item base price from the API.</param>
        public static double Calculate(double requirementsPrice, double basePrice)
        {
            if (basePrice == 0d || requirementsPrice == 0d)
                return 0d;

            double num2 = CommunityItemTax / 100d;
            double num3 = CommunityRequirementTax / 100d;
            double num4 = Math.Log10(basePrice / requirementsPrice);
            double num5 = Math.Log10(requirementsPrice / basePrice);

            if (requirementsPrice >= basePrice)
                num5 = Math.Pow(num5, 1.08d);
            else
                num4 = Math.Pow(num4, 1.08d);

            num4 = Math.Pow(4.0d, num4);
            num5 = Math.Pow(4.0d, num5);

            double tax = basePrice * num2 * num4 + requirementsPrice * num3 * num5;
            return tax * RagFairCommissionModifier;
        }
    }
}
