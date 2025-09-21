﻿using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using System.Linq;
using System;
using System.Text;

namespace traitacquirer
{
    public class traitacquirerModSystem : ModSystem
    {
        // Called on server and client
        // Useful for registering block/entity classes on both sides
        ICoreAPI api;
        ICoreClientAPI capi;
        ICoreServerAPI sapi;
        
        public List<ExtendedTrait> traits = new List<ExtendedTrait>();
        public List<CharacterClass> characterClasses = new List<CharacterClass>();
        public Dictionary<string, ExtendedTrait> TraitsByCode = new Dictionary<string, ExtendedTrait>();
        public Dictionary<string, CharacterClass> characterClassesByCode = new Dictionary<string, CharacterClass>();
        GuiDialogCharacterBase charDlg;
        
        GuiElementRichtext richtextElem;
        ElementBounds clippingBounds;
        ElementBounds scrollbarBounds;
        int spacing = 5;
        public override void Start(ICoreAPI api)
        {
            this.api = api;
            //Register Classes
            api.RegisterItemClass(Mod.Info.ModID + ".ItemTraitManual", typeof(ItemTraitManual));
            //Load Config
            traitacquirerConfig.loadConfig(api);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.sapi = api;
            loadCharacterClasses();
            api.Event.RegisterEventBusListener(AcquireTraitEventHandler, 0.5, "traitItem");
            acquireTraitCommand();
            giveTraitCommand();
            //listTraitsCommand();
        }

        public void acquireTraitCommand()
        {
            var parsers = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.GetOrCreate("acquireTrait")
            .WithAlias("at")
            .WithDescription(Lang.Get("traitacquirer-acquiretraitcommand-desc"))//"Gives the caller the given Trait, removes with the rm flag, overrides exclusivity with the force flag")
            .RequiresPrivilege(this.api.World.Config.GetString("acquireCmdPrivilege"))
            .RequiresPlayer()
            .WithArgs(parsers.Word("trait name"), parsers.OptionalBool("remove flag", "rm"), parsers.OptionalBool("force flag", "f"))
            .HandleWith((args) =>
            {
                var byEntity = args.Caller.Entity;
                string exitMessage;
                string traitName = args[0].ToString();
                bool success;
                bool remove = false;
                bool force = false;
                if (!args.Parsers[1].IsMissing) { remove = (bool)args[1]; }
                if (!args.Parsers[2].IsMissing) { force = (bool)args[2]; }
                if (traits.Find(x => x.Code == traitName) == null)
                {
                    return TextCommandResult.Error("Trait does not exist");
                }
                IPlayer byPlayer = null;
                if (byEntity is EntityPlayer) byPlayer = byEntity.World.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);
                if (remove)
                {
                    success = processTraits(byPlayer?.PlayerUID, new string[0], new string[] { traitName }, force);
                    exitMessage = "Trait Removed";
                }
                else
                {
                    success = processTraits(byPlayer?.PlayerUID, new string[] { traitName }, new string[0], force);
                    exitMessage = "Trait given";
                }
                if (!success)
                {
                    return TextCommandResult.Error("Unable to execute Command");
                }
                return TextCommandResult.Success(exitMessage);
            });
        }

        public void giveTraitCommand()
        {
            var parsers = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.GetOrCreate("giveTrait")
            .WithAlias("gt")
            .WithDescription(Lang.Get("traitacquirer-givecommand-desc"))//"Gives the given Trait to the chosen player, removes with the rm flag, overrides exclusivity with the force flag")
            .RequiresPrivilege(this.api.World.Config.GetString("giveCmdPrivilege"))
            .RequiresPlayer()
            .WithArgs(parsers.Word("trait name"), parsers.OnlinePlayer("target player"), parsers.OptionalBool("remove flag", "rm"), parsers.OptionalBool("force flag", "f"))
            .HandleWith((args) =>
            {
                IServerPlayer targetPlayer = (IServerPlayer)args[1];
                var byEntity = args.Caller.Entity;
                string exitMessage;
                bool success;
                string traitName = args[0].ToString();
                bool remove = false;
                bool force = false;
                if (!args.Parsers[2].IsMissing) { remove = (bool)args[2]; }
                if (!args.Parsers[3].IsMissing) { force = (bool)args[3]; }
                if (traits.Find(x => x.Code == traitName) == null)
                {
                    return TextCommandResult.Error("Trait does not exist");
                }
                if (remove)
                {
                    success = processTraits(targetPlayer?.PlayerUID, new string[0], new string[] { traitName }, force);
                    exitMessage = "Trait Removed";
                }
                else
                {
                    success = processTraits(targetPlayer?.PlayerUID, new string[] { traitName }, new string[0], force);
                    exitMessage = "Trait Given";
                }
                if (!success)
                {
                    return TextCommandResult.Error("Unable to execute Command");
                }
                return TextCommandResult.Success(exitMessage);
            });
        }

        public void listTraitsCommand()
        {
            var parsers = sapi.ChatCommands.Parsers;
            sapi.ChatCommands.GetOrCreate("listTraits")
            .WithAlias("lt")
            .WithDescription("Returns a sorted list of the loaded trait codes")
            .RequiresPrivilege(this.api.World.Config.GetString("listCmdPrivelege"))
            .RequiresPlayer()
            .HandleWith((args) =>
            {
                List<string> traitList = new();
                foreach (ExtendedTrait trait in traits)
                {
                    traitList.Add(trait.Code);
                }
                traitList.Sort();
                string returnString = "";
                foreach (string traitName in traitList)
                {
                    returnString += $"{traitName}\n";
                }
                return TextCommandResult.Success(returnString);
            });
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            this.capi = api;
            loadCharacterClasses();
            charDlg = api.Gui.LoadedGuis.Find(dlg => dlg is GuiDialogCharacterBase) as GuiDialogCharacterBase;
            charDlg.RenderTabHandlers.Add(composeTraitsTab);
            
            api.Event.BlockTexturesLoaded += cleanupTraitsTab;

            //Generate Handbook Pages
            api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>().OnInitCustomPages += traitacquirerModSystem_OnInitCustomPages;
        }

        public void traitacquirerModSystem_OnInitCustomPages(List<GuiHandbookPage> pages)
        {
            foreach (ExtendedTrait trait in traits) //Generate a page for each trait
            {
                pages.Add(new GuiHandbookExtendedTraitPage(capi, trait));
            }
            foreach (int type in Enum.GetValues(typeof(EnumTraitType))) //Generate a page for each type of trait
            {
                pages.Add(new GuiHandbookTraitTypesPage(capi, type, traits));
            }
        }

        private void cleanupTraitsTab()
        {
            foreach (Action<GuiComposer> i in charDlg.RenderTabHandlers)
            {
                if (i.Target.ToString() == "Vintagestory.GameContent.CharacterSystem")
                {
                    charDlg.RenderTabHandlers.Remove(i);
                    break;
                }
            }
        }

        private void composeTraitsTab(GuiComposer compo)
        {

            this.clippingBounds = ElementBounds.Fixed(0, 25, 385, 310);
            compo.BeginClip(clippingBounds);
            compo.AddRichtext(getClassTraitText(), CairoFont.WhiteDetailText().WithLineHeightMultiplier(1.15), ElementBounds.Fixed(0, 0, 385, 310), "text");
            compo.EndClip();
            this.scrollbarBounds = clippingBounds.CopyOffsetedSibling(clippingBounds.fixedWidth - 3, -6).WithFixedWidth(6).FixedGrow(0, 2);
            compo.AddVerticalScrollbar(OnNewScrollbarValue, this.scrollbarBounds, "scrollbar");
            this.richtextElem = compo.GetRichtext("text");

            compo.GetScrollbar("scrollbar").SetHeights(
                (float)100, (float)310
            );
        }
        private void OnNewScrollbarValue(float value)
        {
            richtextElem.Bounds.fixedY = 10 - value;
            richtextElem.Bounds.CalcWorldBounds();

        }

        string getClassTraitText()
        {
            string charClass = capi.World.Player.Entity.WatchedAttributes.GetString("characterClass");
            CharacterClass chclass = characterClasses.FirstOrDefault(c => c.Code == charClass);

            StringBuilder fulldesc = new StringBuilder();
            StringBuilder attributes = new StringBuilder();

            fulldesc.AppendLine(Lang.Get("Class Traits: "));

            var chartraits = chclass.Traits.Select(code => TraitsByCode[code]).OrderBy(trait => (int)trait.Type);

            foreach (var trait in chartraits)
            {
                attributes.Clear();
                foreach (var val in trait.Attributes)
                {
                    if (attributes.Length > 0) attributes.Append(", ");
                    attributes.Append(Lang.Get(string.Format(GlobalConstants.DefaultCultureInfo, "charattribute-{0}-{1}", val.Key, val.Value)));
                }

                if (attributes.Length > 0)
                {
                    fulldesc.AppendLine(Lang.Get("traitwithattributes", Lang.Get("trait-" + trait.Code), attributes));
                }
                else
                {
                    string desc = Lang.Get("traitdesc-" + trait.Code);
                    if (desc != null)
                    {
                        fulldesc.AppendLine(Lang.Get("traitwithattributes", Lang.Get("trait-" + trait.Code), desc));
                    }
                    else
                    {
                        fulldesc.AppendLine(Lang.Get("trait-" + trait.Code));
                    }


                }
            }

            if (chclass.Traits.Length == 0)
            {
                fulldesc.AppendLine(Lang.Get("No positive or negative traits"));
            }

            fulldesc.AppendLine(Lang.Get("Extra Traits: "));

            string[] extraTraits = capi.World.Player.Entity.WatchedAttributes.GetStringArray("extraTraits");
            IOrderedEnumerable<string> extratraits = Enumerable.Empty<string>().OrderBy(x => 1); ;
            if (extraTraits != null)
            {
                var cleanedTraits = extraTraits.Where(code => TraitsByCode.ContainsKey(code)).ToList();
                extratraits = cleanedTraits?.OrderBy(code => (int)TraitsByCode[code].Type);
            }

            foreach (var code in extratraits)
            {
                attributes.Clear();
                foreach (var val in TraitsByCode[code].Attributes)
                {
                    if (attributes.Length > 0) attributes.Append(", ");
                    attributes.Append(Lang.Get(string.Format(GlobalConstants.DefaultCultureInfo, "charattribute-{0}-{1}", val.Key, val.Value)));
                }

                if (attributes.Length > 0)
                {
                    fulldesc.AppendLine(Lang.Get("traitwithattributes", Lang.Get("trait-" + code), attributes));
                }
                else
                {
                    string desc = Lang.Get("traitdesc-" + code);
                    if (desc != null)
                    {
                        fulldesc.AppendLine(Lang.Get("traitwithattributes", Lang.Get("trait-" + code), desc));
                    }
                    else
                    {
                        fulldesc.AppendLine(Lang.Get("trait-" + code));
                    }


                }
            }

            return fulldesc.ToString();
        }

        public void AcquireTraitEventHandler(string eventName, ref EnumHandling handling, IAttribute data)
        {
            TreeAttribute tree = data as TreeAttribute;
            string playerUid = tree.GetString("playeruid");
            IPlayer player = api.World.PlayerByUid(playerUid);
            string[] addtraits = tree.GetStringArray("addtraits");
            string[] removetraits = tree.GetStringArray("removetraits");
            int itemslotId = tree.GetInt("itemslotId", -1);
            ItemSlot itemslot = player.InventoryManager.GetHotbarInventory()[itemslotId];
            bool success = processTraits(playerUid, addtraits, removetraits);
            if (success)
            {
                itemslot.TakeOut(1);
                itemslot.MarkDirty();
            }
        }

        public bool processTraits(string playerUid, string[] addtraits, string[] removetraits, bool force = false)
        {
            IServerPlayer plr = api.World.PlayerByUid(playerUid) as IServerPlayer;
            List<string> newExtraTraits = new List<string>();
            string[] extraTraits = plr.Entity.WatchedAttributes.GetStringArray("extraTraits");
            List<string> incompatibleTraits = new List<string>();

            //Keep traits already added
            if (extraTraits != null)
            {
                newExtraTraits.AddRange(extraTraits);
            }

            //Remove traits from the updated list
            foreach (string traitName in removetraits)
            {
                ExtendedTrait trait = traits.Find(x => x.Code == traitName);
                if (trait == null)
                {
                    plr.SendIngameError("Trait is Null", Lang.Get("Trait is Null"));
                    return false;
                }
                if (newExtraTraits.Contains(traitName))
                {
                    newExtraTraits.Remove(traitName);
                }
            }

            //Build the new list of traits you'll possess
            foreach (string traitName in addtraits)
            {
                ExtendedTrait trait = traits.Find(x => x.Code == traitName);
                if (trait == null)
                {
                    plr.SendIngameError("Trait is Null", Lang.Get("Trait is Null"));
                    return false;
                }
                if (!newExtraTraits.Contains(traitName))
                {
                    newExtraTraits.Add(traitName);
                }
            }

            if (!force)
            {
                //Determine which traits are incompatible with the updated trait list
                foreach (string traitName in newExtraTraits)
                {
                    ExtendedTrait trait = traits.Find(x => x.Code == traitName);
                    if (trait.ExclusiveWith != null)
                    {
                        incompatibleTraits.AddRange(trait.ExclusiveWith);
                    }
                }

                //Determine whether there are any incompatibilities in the new list and fail the change
                foreach (string traitName in newExtraTraits)
                {
                    if (incompatibleTraits.Contains(traitName))
                    {
                        plr.SendIngameError("Trait is Incompatible", Lang.Get("Trait is Incompatible"));
                        return false;
                    }
                }
            }

            //Update the trait list and apply their effects
            plr.Entity.WatchedAttributes.SetStringArray("extraTraits", newExtraTraits.ToArray());
            plr.Entity.WatchedAttributes.MarkPathDirty("extraTraits");
            applyTraitAttributes(plr.Entity, addtraits, removetraits);
            plr.Entity.World.PlaySoundAt(new AssetLocation("sounds/effect/writing"), plr.Entity);
            return true;
        }

        private void applyTraitAttributes(EntityPlayer eplr, string[] addtraits, string[] removetraits)
        {
            string classcode = eplr.WatchedAttributes.GetString("characterClass");
            CharacterClass charclass = characterClasses.FirstOrDefault(c => c.Code == classcode);
            if (charclass == null) throw new ArgumentException("Not a valid character class code!");

            //Remove trait attributes
            foreach (string traitcode in removetraits)
            {
                ExtendedTrait trait = TraitsByCode[traitcode];
                foreach (var attr in trait.Attributes)
                {
                    eplr.Stats[attr.Key].Remove($"trait_{traitcode}");
                }
            }

            //Add trait attributes
            foreach (string traitcode in addtraits)
            {
                ExtendedTrait trait = TraitsByCode[traitcode];
                foreach (var attr in trait.Attributes)
                {
                    eplr.Stats.Set(attr.Key, $"trait_{traitcode}", (float)attr.Value, true);
                }
            }

            //Mark Dirty
            eplr.GetBehavior<EntityBehaviorHealth>()?.MarkDirty();

            /*
            // Reset 
            foreach (var stat in eplr.Stats)
            {
                foreach (var statmod in stat.Value.ValuesByKey)
                {
                    if (statmod.Key.Length >= 5 ? statmod.Key[..5] == "trait" : false)
                    {
                        stat.Value.Remove(statmod.Key);
                    }
                }
            }

            // Then apply
            string[] extraTraits = eplr.WatchedAttributes.GetStringArray("extraTraits");
            var allTraits = extraTraits == null ? charclass.Traits : charclass.Traits.Concat(extraTraits);

            foreach (var traitcode in allTraits)
            {
                ExtendedTrait trait;
                if (TraitsByCode.TryGetValue(traitcode, out trait))
                {
                    foreach (var val in trait.Attributes)
                    {
                        string attrcode = val.Key;
                        double attrvalue = val.Value;

                        eplr.Stats.Set(attrcode, $"trait_{traitcode}", (float)attrvalue, true);
                    }
                }
            }
            
            eplr.GetBehavior<EntityBehaviorHealth>()?.MarkDirty();
            */
        }
        public void loadCharacterClasses() //Taken from SurvivalMod Character.cs, CharacterSystem class where it is a private method
        {
            //onLoadedUniversal();
            
            // Initialize empty collections
            this.traits = new List<ExtendedTrait>();
            this.characterClasses = new List<CharacterClass>();
            
            // Get all assets and filter for trait and class files
            var allAssets = api.Assets.AllAssets;
            var traitFiles = allAssets.Where(kvp => 
                kvp.Key.Path.StartsWith("config/") && 
                kvp.Key.Path.EndsWith("traits.json"));
            var classFiles = allAssets.Where(kvp => 
                kvp.Key.Path.StartsWith("config/") && 
                kvp.Key.Path.EndsWith("characterclasses.json"));
            
            // Load all trait files
            foreach (var traitFile in traitFiles)
            {
                try
                {
                    var assetTraits = api.Assets.TryGet(traitFile.Value.Location).ToObject<List<ExtendedTrait>>();
                    if (assetTraits != null)
                    {
                        api.World.Logger.Debug($"[TraitAcquirer] Loading {assetTraits.Count} traits from {traitFile.Key.Path} (mod: {traitFile.Key.Domain})");
                        this.traits.AddRange(assetTraits);
                    }
                }
                catch (Exception ex)
                {
                    api.World.Logger.Warning($"[TraitAcquirer] Failed to load traits from {traitFile.Key.Path}: {ex.Message}");
                }
            }
            
            // Load all character class files
            foreach (var classFile in classFiles)
            {
                try
                {
                    var assetClasses = api.Assets.TryGet(classFile.Value.Location).ToObject<List<CharacterClass>>();
                    if (assetClasses != null)
                    {
                        api.World.Logger.Debug($"[TraitAcquirer] Loading {assetClasses.Count} character classes from {classFile.Key.Path} (mod: {classFile.Key.Domain})");
                        this.characterClasses.AddRange(assetClasses);
                    }
                }
                catch (Exception ex)
                {
                    api.World.Logger.Warning($"[TraitAcquirer] Failed to load character classes from {classFile.Key.Path}: {ex.Message}");
                }
            }
            
            // Remove duplicates based on Code (last loaded wins)
            this.traits = this.traits.GroupBy(t => t.Code).Select(g => g.Last()).ToList();
            this.characterClasses = this.characterClasses.GroupBy(c => c.Code).Select(g => g.Last()).ToList();
            
            api.World.Logger.Debug($"[TraitAcquirer] Loaded total: {this.traits.Count} traits, {this.characterClasses.Count} character classes");
            foreach (var trait in traits)
            {
                TraitsByCode[trait.Code] = trait;

                /*string col = "#ff8484";
                if (trait.Type == EnumTraitType.Positive) col = "#84ff84";
                if (trait.Type == EnumTraitType.Mixed) col = "#fff584";

                Console.WriteLine("\"trait-" + trait.Code + "\": \"<font color=\\"" + col + "\\">• " + trait.Code + "</font> ({0})\",");*/

                /*foreach (var val in trait.Attributes)
                {
                    Console.WriteLine("\"charattribute-" + val.Key + "-"+val.Value+"\": \"\",");
                }*/
            }

            foreach (var charclass in characterClasses)
            {
                characterClassesByCode[charclass.Code] = charclass;

                foreach (var jstack in charclass.Gear)
                {
                    if (!jstack.Resolve(api.World, "character class gear", false))
                    {
                        api.World.Logger.Warning("Unable to resolve character class gear " + jstack.Type + " with code " + jstack.Code + " item/bloc does not seem to exist. Will ignore.");
                    }
                }
            }
        }
    }
}
