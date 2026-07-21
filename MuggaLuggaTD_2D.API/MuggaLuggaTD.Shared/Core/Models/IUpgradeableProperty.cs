namespace Core.Models
{
    public interface IUpgradeableProperty<T>
    {
        public T BaseValue { get; set; }
        public T AdjustedBaseValue { get; set; }
        public T AdjustedValue { get; set; }
    }
}