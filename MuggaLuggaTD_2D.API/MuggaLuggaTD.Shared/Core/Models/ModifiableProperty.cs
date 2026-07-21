namespace Core.Models
{
    public class ModifiableProperty<T> {
        public T BaseValue { get; set; }
        public T AdjustedBaseValue { get; set; }
        public T AdjustedValue { get; set; }
    }
}