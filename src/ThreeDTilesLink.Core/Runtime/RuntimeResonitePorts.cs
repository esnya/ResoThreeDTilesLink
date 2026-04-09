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
            InteractiveInputStore = session;
        }

        internal ResoniteSession Session { get; }

        internal IResoniteSessionConnection Connection { get; }

        internal IResoniteSceneWriter SceneWriter { get; }

        internal IResoniteSession SessionControl => Session;

        internal IResoniteSession InteractiveSession => Session;

        internal IResoniteSessionMetadataPort SessionMetadata { get; }

        internal IInteractiveInputStore InteractiveInputStore { get; }
    }
}
