using System;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("No Blood", "birthdates", "1.0.1")]
    [Description("Removes bleeding from rust")]
    public class NoBlood : RustPlugin
    {
        #region Variables
        private string Perm = "noblood.use";
        #endregion

        #region Hooks
        private void Init()
        {
            LoadConfig();
            permission.RegisterPermission(Perm, this);
        }

        void OnRunPlayerMetabolism(PlayerMetabolism metabolism)
        {
            if (metabolism.bleeding.value < 1) return;
            var p = metabolism.GetComponent<BasePlayer>();
            if (p == null || !permission.UserHasPermission(p.UserIDString, Perm)) return;
            metabolism.bleeding.value = 0;
        }
        #endregion
    }
}
//Generated with birthdates' Plugin Maker
