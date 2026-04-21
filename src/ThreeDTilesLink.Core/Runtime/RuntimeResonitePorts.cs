using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Core.Runtime
{
    internal sealed class RuntimeResonitePorts
    {
        internal RuntimeResonitePorts(ResoniteSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            Session = session;
            Connection = session;
            SceneWriter = session;
            SessionMetadata = session;
            InteractiveUiStore = new ResoniteDynamicValueInteractiveUiStore(session);
        }

        internal ResoniteSession Session { get; }

        internal ISceneConnection Connection { get; }

        internal ISceneWriter SceneWriter { get; }

        internal IResoniteSession SessionControl => Session;

        internal IResoniteSession InteractiveSession => Session;

        internal ISceneMetadataSink SessionMetadata { get; }

        internal IInteractiveUiStore InteractiveUiStore { get; }
    }
}
