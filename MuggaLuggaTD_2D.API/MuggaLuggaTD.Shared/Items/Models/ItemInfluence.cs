using Enums;

namespace Items.Models
{
    /// <summary>
    /// Represents an influence on an item.
    /// Influences add affinity-specific bonus attributes based on star level.
    /// An item can have multiple influences with different affinities.
    /// </summary>
    public class ItemInfluence
    {
        /// <summary>
        /// The affinity type of this influence.
        /// </summary>
        public AffinityTypes AffinityType { get; set; }

        /// <summary>
        /// The star level of the influence (1-5).
        /// Higher stars provide larger influence bonuses.
        /// </summary>
        public int Stars { get; set; }
    }
}
