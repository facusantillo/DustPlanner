# HDT API check

- Folder: `C:\Users\Pupunete\AppData\Local\HearthstoneDeckTracker\app-1.59.2`
- hdt: `HearthstoneDeckTracker` 1.59.2.9014
- mirror: `HearthMirror` 32.4.2.0
- db: `HearthDb` 37.0.0.0

| Status | Assembly | Type | Member | Found signature | Used for |
|---|---|---|---|---|---|
| ok | hdt | `Hearthstone_Deck_Tracker.Plugins.IPlugin` | `OnLoad` | `Void OnLoad()` | plugin contract |
| ok | hdt | `Hearthstone_Deck_Tracker.Plugins.IPlugin` | `MenuItem` | `MenuItem MenuItem` | plugin contract |
| ok | hdt | `Hearthstone_Deck_Tracker.API.Core` | `Game` | `GameV2 Game` | IsRunning |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.GameV2` | `IsRunning` | `Boolean IsRunning` | skip memory reads when the game is closed |
| ok | hdt | `Hearthstone_Deck_Tracker.API.GameEvents` | `OnModeChanged` | `ActionList`1 OnModeChanged` | export on menu transitions |
| ok | hdt | `Hearthstone_Deck_Tracker.API.ActionList`1` | `Add` | `Void Add(Action`1 action)` | subscribe to OnModeChanged |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.CollectionHelpers` | `Hearthstone` | `CollectionHelper`1 Hearthstone` | collection access |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.CollectionHelper`1` | `GetCollection` | `Task`1 GetCollection()` | load collection |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.CollectionHelper`1` | `TryGetCollection` | `Boolean TryGetCollection(T& collection)` | cached collection |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.CollectionHelper`1` | `OnCollectionChanged` | `event Action OnCollectionChanged` | export trigger |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Collection` | `Cards` | `SortedDictionary`2 Cards` | dbfId -> int[8] |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Collection` | `Dust` | `Int32 Dust` | dust balance |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.CollectionBase` | `AccountHi` | `UInt64 AccountHi` | account key |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.CollectionBase` | `AccountLo` | `UInt64 AccountLo` | account key |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.CollectionBase` | `BattleTag` | `String BattleTag` | display |
| ok | hdt | `Hearthstone_Deck_Tracker.DeckList` | `Instance` | `DeckList Instance` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.DeckList` | `Decks` | `ObservableCollection`1 Decks` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Deck` | `GetSelectedDeckVersion` | `Deck GetSelectedDeckVersion()` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Deck` | `DeckId` | `Guid DeckId` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Deck` | `Sideboards` | `List`1 Sideboards` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Deck` | `IsWildDeck` | `Boolean IsWildDeck` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Deck` | `IsArenaDeck` | `Boolean IsArenaDeck` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Sideboard` | `OwnerCardId` | `String OwnerCardId` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Hearthstone.Card` | `DbfId` | `Int32 DbfId` | HDT saved decks |
| ok | hdt | `Hearthstone_Deck_Tracker.Utility.Logging.Log` | `Info` | `Void Info(String msg, String memberName, String sourceFilePath)` | logging |
| ok | mirror | `HearthMirror.Reflection` | `Client` | `IReflection Client` | all HearthMirror reads |
| ok | mirror | `HearthMirror.Reflection` | `ClientReadTimeout` | `IDisposable ClientReadTimeout(Int32 timeoutMillis)` | bounded memory reads |
| ok | mirror | `HearthMirror.IReflection` | `GetFullCollection` | `Collection GetFullCollection()` | diagnostics (raw premium types, gold) |
| ok | mirror | `HearthMirror.IReflection` | `GetDecks` | `List`1 GetDecks()` | in-game decks |
| ok | mirror | `HearthMirror.IReflection` | `GetMedalInfo` | `MedalInfo GetMedalInfo()` | rank |
| ok | mirror | `HearthMirror.IReflection` | `GetAccountId` | `AccountId GetAccountId()` | account key |
| ok | mirror | `HearthMirror.IReflection` | `GetBattleTag` | `BattleTag GetBattleTag()` | display |
| ok | mirror | `HearthMirror.Objects.Collection` | `Cards` | `List`1 Cards` | diagnostics |
| ok | mirror | `HearthMirror.Objects.Collection` | `Gold` | `Int32 Gold` | gold balance |
| ok | mirror | `HearthMirror.Objects.Card` | `PremiumType` | `Int32 PremiumType` | diagnostics |
| ok | mirror | `HearthMirror.Objects.Card` | `TrialCount` | `Int32 TrialCount` | diagnostics |
| ok | mirror | `HearthMirror.Objects.Deck` | `Sideboards` | `Dictionary`2 Sideboards` | in-game decks |
| ok | mirror | `HearthMirror.Objects.Deck` | `FormatType` | `Int32 FormatType` | in-game decks |
| ok | mirror | `HearthMirror.Objects.MedalInfo` | `Standard` | `MedalInfoData Standard` | rank |
| ok | mirror | `HearthMirror.Objects.MedalInfoData` | `StarLevel` | `Int32 StarLevel` | rank |
| ok | mirror | `HearthMirror.Objects.MedalInfoData` | `LegendRank` | `Int32 LegendRank` | rank |
| ok | db | `HearthDb.Cards` | `GetFromDbfId` | `Card GetFromDbfId(Int32 dbfId, Boolean collectible)` | card names in diagnostics |
| ok | db | `HearthDb.Cards` | `CardIdToDbfId` | `Dictionary`2 CardIdToDbfId` | cardId -> dbfId |
| ok | db | `HearthDb.Cards` | `All` | `Dictionary`2 All` | hero class lookup |
| ok | db | `HearthDb.Card` | `Set` | `CardSet Set` | Core set detection |

## APIs expected to be absent

- `HearthMirror.IReflection` members matching `Pack|Booster`: `List`1 GetPackCards()`, `Int32 GetLastOpenedBoosterId()`
  - No API exposes the unopened pack count; only GetPackCards (the pack being opened) and GetLastOpenedBoosterId exist.

**Result: all required APIs present.**
