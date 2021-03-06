using AElf.Common;
using AElf.Common.Serializers;
using AElf.Database;

namespace AElf.Kernel.Storages
{
    public class GenesisBlockHashStore : KeyValueStoreBase, IGenesisBlockHashStore
    {
        public GenesisBlockHashStore(IKeyValueDatabase keyValueDatabase, IByteSerializer byteSerializer)
            : base(keyValueDatabase, byteSerializer, GlobalConfig.GenesisBlockHashPrefix)
        {
        }
    }
}
