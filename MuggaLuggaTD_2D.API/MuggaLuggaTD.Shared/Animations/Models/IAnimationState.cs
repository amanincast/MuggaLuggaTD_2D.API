using Enums;

namespace MuggaLuggaTD.Shared.Animations
{
    public interface IAnimationState
    {
        bool IsMoving { get; set; }
        AnimationDirections CurrentDirection { get; set; }
        string CurrentAnimation { get; set; }
        bool PreventLooping { get; set; }

        string GetNextAnimationToPlay();
    }
}