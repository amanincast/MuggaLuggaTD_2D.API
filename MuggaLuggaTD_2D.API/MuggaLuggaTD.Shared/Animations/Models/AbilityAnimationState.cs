using Enums;

namespace MuggaLuggaTD.Shared.Animations
{
    public class AbilityAnimationState : IAnimationState
    {
        public bool IsMoving { get; set; }
        public AnimationDirections CurrentDirection { get; set; } = AnimationDirections.Right;
        public string CurrentAnimation { get; set; }
        public bool PreventLooping { get; set; } = false;
        public int ActivationFrameCount { get; set; } = 1;
        public string GetNextAnimationToPlay()
        {
            return GetNextAnimationPrefix() + GetNextAnimationSuffix();
        }

        protected string GetNextAnimationPrefix()
        {
            switch (ActivationFrameCount)
            {
                case 1:
                    return "OneFrameActivate";
                case 2:
                    return "TwoFrameActivate";
                case 3:
                    return "ThreeFrameActivate";
                case 4:
                    return "FourFrameActivate";
                case 5:
                    return "FiveFrameActivate";
                case 6:
                    return "SixFrameActivate";
                case 7:
                    return "SevenFrameActivate";
                case 8:
                    return "EightFrameActivate";
                case 9:
                    return "NineFrameActivate";
                case 10:
                    return "TenFrameActivate";
            }

            return "OneFrameActivate";
        }

        protected string GetNextAnimationSuffix()
        {
            if (this.PreventLooping)
                return "_NoLoop";
            return "";
        }
    }
}