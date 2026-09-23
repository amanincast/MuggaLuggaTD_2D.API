namespace Enums
{
    /// <summary>
    /// How far a character's signature can eventually be taken.
    ///
    /// <para>Rarity deliberately does <b>not</b> change base stats: two Warriors with the same
    /// signature and affinity fight identically at level 1, and a Common with the combination you
    /// wanted is still worth keeping. What rarity buys is the ceiling - which awakening stages the
    /// signature can reach. Design doc 05 §1.</para>
    ///
    /// <para>Nothing reads it for effect yet; the stages it gates arrive with awakening. It is part
    /// of the identity now because the identity is what the Tavern rolls and the save is checked
    /// against, and splitting the triple across two save formats would buy nothing.</para>
    /// </summary>
    public enum CharacterRarity : short
    {
        Common = 0,
        Rare = 1,
        Epic = 2,
        Legendary = 3
    }
}
