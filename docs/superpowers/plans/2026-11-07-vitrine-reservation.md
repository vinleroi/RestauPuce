# ChefOS Étape 4 — Vitrine, vins, réservation, prépaiement : plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un site public par restaurant (ambiance, menu, carte des vins) avec réservation par créneau, pré-commande nominative pour les groupes et acompte ou prépaiement en ligne, plus les écrans du back correspondants.

**Architecture:**
- Les règles pures vivent dans `ChefOS.Core.Vitrine` : disponibilité par service, montants à payer, règle d'annulation, fuseau horaire.
- Infrastructure porte les entités, les services (`VitrineService`, `WineService`, `ReservationService`, `PaymentService`), la passerelle de paiement (`IPaymentGateway` : Demo, Mollie) et la rédaction de fiches (`IWineDescriber` : Claude, Demo).
- `ChefOS.Vitrine` résout le restaurant par son slug et rend des pages SSR, avec un composant interactif pour la réservation.
- Le back reçoit les pages réglages vitrine, vins et réservations.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, Blazor (SSR + Interactive Server), MudBlazor (back), SDK Anthropic C#, API REST Mollie v2 (HttpClient), xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-07-vitrine-reservation-design.md`

## Global Constraints

- **Branche** : `feature/etape-4-vitrine`.
- **Contraintes reprises des étapes 1 à 3** :
  - `net10.0`, paquets ajoutés sans version, `ChefOS.Core` sans aucun paquet ;
  - interface en français, culture `fr-BE`, tout `MudNumericField` avec `Culture="Ui.Fr"` ;
  - taux en fraction ;
  - calculs en `decimal` non arrondis, l'arrondi au centime n'intervenant que pour un montant à **payer** (`Math.Round(x, 2, AwayFromZero)`) ;
  - dates en UTC en base ;
  - commits `feat:`/`fix:` terminés par `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Heure locale** :
  - `Restaurant.TimeZoneId` (string, défaut `"Europe/Brussels"`) ;
  - conversion via `TimeZoneInfo.FindSystemTimeZoneById` dans un helper pur `RestaurantClock` ;
  - les services lisent l'heure via `TimeProvider`, injecté avec `TimeProvider.System` par défaut et `FakeTimeProvider` dans les tests (paquet `Microsoft.Extensions.TimeProvider.Testing` côté Tests).
- **Multi-restaurant** :
  - toute nouvelle entité du restaurant implémente `ITenantScoped`, reçoit son filtre global et est seedée dans `TenantModelTests.SeedEveryTenantScopedTypeAsync` ;
  - `WineReference` est **globale** (pas `ITenantScoped`, aucun filtre, lecture seule pour les restaurants) ;
  - les services vérifient via le contexte filtré tout id référencé.
- **Vitrine sans utilisateur connecté** :
  - un `SlugTenantContext` scoped (`ITenantContext`) reçoit l'id du restaurant résolu depuis le slug de la route, et seulement si la vitrine est publiée ;
  - tout accès au restaurant passe par lui ;
  - une vitrine non publiée ou un slug inconnu renvoie 404.
- **Secrets** :
  - `Mollie:ApiKey` et `Anthropic:ApiKey` viennent de user-secrets ou de variables d'environnement, jamais de `appsettings*.json` ;
  - les modes se choisissent par `Payments:Gateway` = `Demo` (défaut) | `Mollie`, et `Wines:Describer` = `Demo` (défaut) | `Claude` ;
  - aucun test n'appelle une API réelle.
- **Précisions de colonnes** :
  - montants en euros `numeric(18,2)` ;
  - pourcentages et taux `numeric(5,4)` ;
  - textes longs en `text`.
- **Images** :
  - stockées en base (`bytea`, table dédiée) ;
  - types JPEG, PNG ou WebP, 5 Mo maximum ;
  - réduites dans le navigateur avant l'envoi (réutiliser le module JS des tickets, étape 2) ;
  - servies par la vitrine avec `nosniff` et un cache public.

## Review Focus

1. **Surréservation** : deux visiteurs qui réservent en même temps les dernières places d'un service ne peuvent pas dépasser la capacité.
   → test `Create_Concurrent_LastSeats_NoOverbooking` (Task 5).
2. **Heure d'été** : un créneau à 19:00 heure de Bruxelles le jour du changement d'heure (dernier dimanche d'octobre) est stocké et affiché correctement.
   → test `Clock_DstTransition_RoundTrips` (Task 1).
3. **Acompte et prépaiement** :
   - 2 couverts à 10 € donnent 20,00 € ;
   - un groupe de 8 avec 216,40 € TTC de plats pré-commandés, à 30 %, donne un prépaiement de 64,92 € et aucun acompte par personne en plus.
   → test `Pricing_SmallAndLargeGroups` (Task 1).
4. **Paiement abandonné** : une réservation en attente de paiement de plus de 30 minutes ne bloque plus de places, et un paiement confirmé arrivé après ce délai est remboursé automatiquement (ou marqué « À rembourser ») si la place a été reprise.
   → tests `Availability_IgnoresExpiredPending` (Task 1) et `Payment_PaidAfterExpiryAndSeatTaken_Refunded` (Task 5).
5. **Vitrine non publiée** : aucune page, image ou API de réservation n'est accessible pour un restaurant dont la vitrine n'est pas publiée.
   → test `Vitrine_Unpublished_Returns404` (Task 7) et test de service `Create_UnpublishedRestaurant_Refused` (Task 5).

---

### Task 1 : Domaine (Core) — entités vitrine, vins, réservations ; règles pures

**Files:**
- Create : `src/ChefOS.Core/Vitrine/{VitrineProfile,VitrineImage,ServiceSchedule,ClosureDay,Reservation,ReservationStatus,ReservationGuest,ReservationGuestDish,Payment,PaymentKind,PaymentStatus,WineReference,WineColor,WineListItem,WineListItemPairing}.cs`, `src/ChefOS.Core/Vitrine/{RestaurantClock,AvailabilityCalculator,ReservationPricing,CancellationPolicy}.cs`
- Modify : `src/ChefOS.Core/Restaurants/Restaurant.cs` (`TimeZoneId = "Europe/Brussels"`), `src/ChefOS.Core/Recipes/Recipe.cs` (`PublicDescription?`, `ShowOnVitrine` bool)
- Test : `tests/ChefOS.Tests/Core/Vitrine/{RestaurantClockTests,AvailabilityCalculatorTests,ReservationPricingTests,CancellationPolicyTests}.cs`

**Interfaces:**
- Produces (toutes les entités ont `Id`, et `RestaurantId` quand elles sont `ITenantScoped`) :
  - `VitrineProfile : ITenantScoped` (1-1 restaurant) :
    - contenu : `IsPublished`, `Tagline?`, `AmbianceText?`, `AccentColor?` (#RRGGBB), `CoverImageId?`, `City?`, `Phone?`, `PublicEmail?` ;
    - réservation : `ReservationsOpen`, `GroupThreshold = 5`, `DepositPerPerson = 10m`, `PrepaymentPercent = 0.30m`, `FreeCancellationHours = 24`, `MinLeadMinutes = 120`, `MaxDaysAhead = 60`, `MaxPartySize = 20`, `PendingPaymentMinutes = 30`.
  - `VitrineImage : ITenantScoped` : `FileName`, `ContentType`, `byte[] Data`, `Position`, `IsCover`.
  - `ServiceSchedule : ITenantScoped` : `DayOfWeek`, `Name`, `StartLocal` (TimeOnly), `EndLocal` (TimeOnly, strictement après `StartLocal`), `Capacity` (int), `SlotMinutes` (int, 15/30/60).
  - `ClosureDay : ITenantScoped` : `DateLocal` (DateOnly), `Reason?`.
  - `enum ReservationStatus { PendingPayment, Confirmed, Seated, NoShow, Cancelled, Expired }`.
  - `Reservation : ITenantScoped` :
    - `PublicToken` (string, 32+ caractères url-safe), `StartsAtUtc`, `ServiceName`, `PartySize` ;
    - `CustomerName`, `CustomerEmail`, `CustomerPhone`, `Note?` ;
    - `Status`, `CreatedAt`, `ConfirmedAt?`, `CancelledAt?`, `AmountDue` (decimal), `PreorderTotalInclVat` (decimal), `OrderId?` ;
    - `IsManual` (bool), `List<ReservationGuest> Guests`.
  - `ReservationGuest : ITenantScoped` : `ReservationId`, `Position`, `FirstName?`, `LastName?`, `List<ReservationGuestDish> Dishes`.
  - `ReservationGuestDish : ITenantScoped` : `GuestId`, `RecipeId`, `Quantity` (int ≥ 1), `UnitPriceInclVat`, `VatRate` (figés).
  - `enum PaymentKind { Deposit, Prepayment }` ; `enum PaymentStatus { Open, Paid, Failed, Expired, Canceled, RefundPending, Refunded }`.
  - `Payment : ITenantScoped` : `ReservationId`, `Kind`, `Amount`, `Status`, `Gateway` (string), `GatewayPaymentId?`, `CheckoutUrl?`, `CreatedAt`, `PaidAt?`, `RefundedAt?`, `RefundAmount?`.
  - `enum WineColor { Rouge, Blanc, Rose, Effervescent, Doux }`.
  - `WineReference` (**pas** `ITenantScoped`) : `Key` (string unique, slug stable), `Name`, `Appellation?`, `Region`, `Country`, `Color`, `Grapes`, `Robe`, `Nez`, `Bouche`, `Garde`, `ServiceTemperature`, `Pairings`.
  - `WineListItem : ITenantScoped` :
    - `WineReferenceId?`, `Name`, `Producer?`, `Vintage?` (int), `Appellation?`, `Region`, `Country`, `Color`, `Grapes` ;
    - `Robe`, `Nez`, `Bouche`, `Garde`, `ServiceTemperature`, `GeneralPairings` ;
    - `PersonalNote?`, `PriceBottle` (decimal), `PriceGlass?`, `IsPublished`, `DescriptionIsAiDraft` (bool), `Position`.
  - `WineListItemPairing : ITenantScoped` : `WineListItemId`, `RecipeId`.
  - `static class RestaurantClock` :
    - `DateTime ToUtc(DateOnly date, TimeOnly time, string timeZoneId)` : une heure inexistante (saut de printemps) est décalée vers l'avant, une heure ambiguë (retour d'automne) prend la **première** occurrence ;
    - `(DateOnly Date, TimeOnly Time) ToLocal(DateTime utc, string timeZoneId)` ;
    - `DateOnly Today(DateTime utcNow, string timeZoneId)`.
  - `record Slot(DateOnly Date, TimeOnly Time, string ServiceName, DateTime StartsAtUtc, int RemainingCovers)`.
  - `record BookedCovers(string ServiceName, DateOnly Date, int Covers)`.
  - `static class AvailabilityCalculator` : `IReadOnlyList<Slot> SlotsFor(DateOnly date, IReadOnlyList<ServiceSchedule> schedules, IReadOnlyList<ClosureDay> closures, IReadOnlyList<(DateTime StartsAtUtc, string ServiceName, int PartySize, ReservationStatus Status, DateTime CreatedAt)> reservations, VitrineProfile rules, string timeZoneId, DateTime utcNow, int partySize)` :
    - une date fermée ou un jour sans service ne donne aucun créneau ;
    - les créneaux vont de `StartLocal` à `EndLocal` exclu, par pas de `SlotMinutes` ;
    - les créneaux avant `utcNow + MinLeadMinutes` ou au-delà de `MaxDaysAhead` jours sont exclus ;
    - restant d'un service = `Capacity − Σ PartySize` des réservations de ce service et de cette date locale dont le statut est `Confirmed` ou `Seated`, ou `PendingPayment` créée il y a moins de `PendingPaymentMinutes` ;
    - un créneau n'est renvoyé que si restant ≥ `partySize` ;
    - le restant est porté par chaque créneau.
  - `record AmountToPay(decimal Amount, PaymentKind? Kind)`.
  - `static class ReservationPricing` : `AmountToPay Compute(int partySize, decimal preorderTotalInclVat, VitrineProfile rules)` :
    - si `partySize > GroupThreshold` : `Prepayment = round2(preorderTotal × PrepaymentPercent)` ;
    - sinon : `Deposit = round2(DepositPerPerson × partySize)` ;
    - un montant de 0 donne `Kind = null`.
  - `static class CancellationPolicy` : `bool IsFreeCancellation(DateTime startsAtUtc, DateTime utcNow, int freeCancellationHours)` → `utcNow <= startsAtUtc − hours`.

- [ ] **Step 1 : Tests qui échouent.**
  - `Clock_DstTransition_RoundTrips` : 19:00 le 25/10/2026 et le 29/03/2026 à Bruxelles ; une heure inexistante (02:30 le 29/03) est décalée vers 03:30.
  - `Clock_Today_UsesLocalDate` : 23:30 UTC le 31/12 donne le 01/01 à Bruxelles.
  - `Availability_SlotsAndRemaining`.
  - `Availability_ClosedDayAndNoService_Empty`.
  - `Availability_LeadTimeAndMaxDays`.
  - `Availability_IgnoresExpiredPending` (Review Focus 4).
  - `Availability_CancelledAndNoShowDontCount`.
  - `Availability_PartyTooBig_NoSlot`.
  - `Pricing_SmallAndLargeGroups` (Review Focus 3).
  - `Pricing_ZeroDeposit_NoPayment`.
  - `Cancellation_BoundaryInclusive`.
- [ ] **Step 2 → échec** · **Step 3 : Implémenter** · **Step 4 → PASS**, suite complète comprise.
- [ ] **Step 5 : Commit** `feat: domaine vitrine, vins et réservations ; disponibilités, montants et fuseau horaire`

---

### Task 2 : Persistance, bibliothèque de vins, migration

**Files:**
- Create : configurations EF des 14 nouvelles entités ; migration `AddVitrine` ; `src/ChefOS.Infrastructure/Wines/wine-library.json` (ressource intégrée, environ 60 vins) ; `src/ChefOS.Infrastructure/Wines/WineLibrarySeeder.cs`
- Modify : `ChefOsDbContext` (DbSets + filtres globaux pour les entités `ITenantScoped` ; `WineReferences` sans filtre), `DependencyInjection` (`AddChefOsInfrastructure` enregistre `TimeProvider.System` si absent et le `WineLibrarySeeder`), `TenantModelTests` (seed des nouveaux types)

Règles :
- **Index uniques** :
  - `VitrineProfile.RestaurantId` ;
  - `Reservation.PublicToken` (global) ;
  - `WineReference.Key` ;
  - (`WineListItemPairing.WineListItemId`, `RecipeId`) ;
  - (`ServiceSchedule.RestaurantId`, `DayOfWeek`, `Name`) ;
  - (`ClosureDay.RestaurantId`, `DateLocal`).
- **Index de recherche** sur `Reservation` (`RestaurantId`, `StartsAtUtc`).
- **Contraintes** :
  - `CK_ServiceSchedule_Times` : `EndLocal > StartLocal`, `Capacity > 0`, `SlotMinutes IN (15,30,60)` ;
  - `CK_ReservationGuestDish_Quantity` : `Quantity >= 1`.
- **Règles de suppression** :
  - `ReservationGuest` → `Reservation` et `ReservationGuestDish` → `Guest` : Cascade ;
  - `ReservationGuestDish` → `Recipe` : Restrict ;
  - `Payment` → `Reservation` : Cascade ;
  - `WineListItem` → `WineReference` : SetNull ;
  - `WineListItemPairing` → `WineListItem` et → `Recipe` : Cascade ;
  - `Reservation.OrderId` → `Order` : SetNull ;
  - `VitrineProfile.CoverImageId` → `VitrineImage` : SetNull ;
  - toutes les entités `ITenantScoped` → `Restaurant` : Cascade.
- **Énumérations** stockées en texte.
- **`RecipeService.DeleteAsync`** refuse aussi une recette présente dans des pré-commandes : « Cette recette figure dans des réservations : archivez-la ».
- **Bibliothèque de vins** :
  - le JSON contient des fiches réalistes et rédigées en français, couvrant les couleurs et les grandes régions, au moins 60 vins avec des `Key` uniques ;
  - `WineLibrarySeeder.SeedAsync` fait un upsert par `Key` (insertion ou mise à jour des champs) et tourne au démarrage du back et de la vitrine, dans tous les environnements.

- [ ] **Step 1 :** ajouter les DbSets sans filtre → `TenantModelTests` échoue (garde-fou). **Step 2 :** filtres, configurations, seed des tests, migration (vérifier les contraintes et les index dans `Up()`).
- [ ] **Step 3 : Tests**
  - `WineLibrary_Seed_IsIdempotentAndComplete` : au moins 60 vins, clés uniques, tous les champs renseignés, une seconde exécution sans doublon ;
  - `WineReference_HasNoTenantFilter` ;
  - `ServiceSchedule_InvalidTimes_Rejected` ;
  - `ReservationToken_Unique` ;
  - `DeleteRecipeInPreorders_Refused` ;
  - `DeleteRestaurant_CascadesVitrineData`.
- [ ] **Step 4 :** suite complète → PASS, aucun avertissement. **Step 5 : Commit** `feat: persistance vitrine, réservations et bibliothèque de vins`

---

### Task 3 : `VitrineService` et réglages vitrine dans le back

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/VitrineService.cs` (+ entrées dans `Inputs.cs`) ; `src/ChefOS.Back/Components/Pages/Vitrine/{VitrineSettings.razor, VitrineSettingsForm.cs, ScheduleEditor.razor}` ; endpoint d'aperçu des images dans le back
- Modify : `Components/Pages/Recipes/RecipeEditor.razor` et `RecipeDraft.cs` (champs « Description publique » et « Afficher sur la vitrine »), `RecipeService` (persistance de ces deux champs), `NavMenu.razor` (section « Vitrine » : Réglages, Carte des vins, Réservations)
- Test : `tests/ChefOS.Tests/Infrastructure/VitrineServiceTests.cs`, `tests/ChefOS.Tests/Back/VitrineSettingsFormTests.cs`

**Interfaces:**
- Produces : `class VitrineService(IDbContextFactory<ChefOsDbContext> factory)` :
  - `Task<VitrineProfile> GetOrCreateProfileAsync()` : profil du restaurant courant, créé avec les valeurs par défaut s'il n'existe pas.
  - `Task SaveProfileAsync(VitrineProfileInput)` :
    - `AccentColor` au format `#RRGGBB` ;
    - pourcentage entre 0 et 1, acompte ≥ 0, seuil ≥ 1, taille maximale entre seuil et 100, délais ≥ 0 ;
    - `TimeZoneId` valide (`FindSystemTimeZoneById`), enregistré sur `Restaurant` ;
    - messages en français.
  - `Task<int> AddImageAsync(byte[] data, string fileName, string contentType, bool isCover)` (types et 5 Mo vérifiés, positions de 0 à 5, maximum 7 images au total), `RemoveImageAsync(int id)`, `MoveImageAsync(int id, int newPosition)`.
  - `Task<IReadOnlyList<ServiceSchedule>> GetSchedulesAsync()`, `Task SaveSchedulesAsync(IReadOnlyList<ServiceScheduleInput>)` :
    - remplacement complet, tout ou rien ;
    - validation : fin après le début, capacité entre 1 et 500, pas de 15, 30 ou 60 ;
    - deux services du même jour ne se chevauchent pas (« Les services Midi et Soir du lundi se chevauchent »).
  - `Task<IReadOnlyList<ClosureDay>> GetClosuresAsync()`, `AddClosureAsync(DateOnly, string?)`, `RemoveClosureAsync(int)`.
  - **Lecture publique** (le contexte est déjà scopé au restaurant par la vitrine) :
    - `Task<PublicVitrine?> GetPublicAsync()` : profil publié, nom, adresse, images, menu publié (plats `ShowOnVitrine`, non archivés, avec prix, groupés par catégorie, allergènes calculés via `RecipeBook.Allergens`) et 3 vins publiés en aperçu ;
    - `Task<VitrineImage?> GetPublicImageAsync(int id)`.
- **Écran `/vitrine`** :
  - onglets Présentation (publication, thème, ambiance, couleur, coordonnées, couverture, galerie avec réduction dans le navigateur), Réservations (règles et montants), Services (éditeur par jour : ajouter, dupliquer sur la semaine), Fermetures ;
  - bouton « Voir ma vitrine » (URL de la vitrine dans la configuration `Vitrine:BaseUrl`, par défaut `http://localhost:5108`) ;
  - `DomainValidationException` affichée en snackbar.

- [ ] **Step 1 : Tests (échec d'abord)**
  - `Profile_DefaultsCreatedOnce` ;
  - `SaveProfile_Validates` ;
  - `Images_TypeSizeAndLimit` ;
  - `Schedules_ReplaceAllOrNothing_AndRejectOverlap` ;
  - `Closures_UniquePerDate` ;
  - `GetPublic_OnlyPublishedDishesAndWines_GroupedWithAllergens` ;
  - `GetPublic_Unpublished_Null` ;
  - `OtherRestaurant_Isolated` ;
  - tests du formulaire.
- [ ] **Step 2 : Implémenter** · **Step 3 :** build sans avertissement et suite complète.
- [ ] **Step 4 : Vérification manuelle** dans le back (compte de démo) : publier, ajouter un service, mettre une image. Nettoyer ensuite.
- [ ] **Step 5 : Commit** `feat: réglages de la vitrine (présentation, services, fermetures, règles de réservation)`

---

### Task 4 : Carte des vins (service, brouillon assisté, écrans du back)

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/WineService.cs`, `src/ChefOS.Infrastructure/Wines/{IWineDescriber,ClaudeWineDescriber,DemoWineDescriber,WineDescriptionJson}.cs`, `src/ChefOS.Back/Components/Pages/Wines/{WineList.razor, WineEditor.razor, WineLibraryDialog.razor, WineForm.cs}`
- Modify : `DependencyInjection.cs` (`AddChefOsWineDescriber(IConfiguration)`), `src/ChefOS.Back/Program.cs`
- Test : `WineServiceTests.cs`, `WineDescriptionJsonTests.cs`, `ClaudeWineDescriberTests.cs` (faux `HttpMessageHandler`, sur le modèle de `ClaudeReceiptReaderTests`), `WineFormTests.cs`

**Interfaces:**
- `class WineService(IDbContextFactory<ChefOsDbContext> factory, IWineDescriber describer)` :
  - `SearchLibraryAsync(string? text, WineColor? color)` : 50 résultats maximum, recherche insensible aux accents et à la casse.
  - `AddFromLibraryAsync(int referenceId, int? vintage, string? producer, decimal priceBottle, decimal? priceGlass)` : copie la fiche.
  - `SaveAsync(WineInput)` : création ou mise à jour d'un vin manuel ou issu de la bibliothèque.
    - Le prix bouteille doit être > 0, le prix verre ≥ 0 ou null, le millésime entre 1900 et l'année courante + 1, et les accords doivent pointer vers des plats du restaurant.
    - Un vin dont `DescriptionIsAiDraft` vaut true ne peut être publié que si l'input le confirme (`ConfirmAiDraftReviewed`), sinon : « Relisez et validez la fiche rédigée par l'assistant avant de la publier ».
  - `SetPublishedAsync`, `DeleteAsync`, `MoveAsync`, `ListAsync()`.
  - `DraftDescriptionAsync(string name, int? vintage, string? producer, string? appellation)` → `WineDraft` (tous les champs de la fiche) ; reçoit les noms des plats publiés pour les accords.
  - Public : `ListPublishedAsync(WineColor? color, string? region)`, `GetPublishedAsync(int id)`.
- `ClaudeWineDescriber` : même schéma que `ClaudeReceiptReader` :
  - `claude-opus-5-5`, effort `Medium`, sortie structurée, `fallbacks: "default"` ;
  - `MaxRetries = 1`, `Timeout = 2 min` ;
  - consigne en français : ne pas inventer de producteur, rester prudent sur le millésime (« caractéristiques typiques »), proposer des accords parmi les plats fournis ;
  - erreurs traduites en français.
- `DemoWineDescriber` : texte générique selon la couleur.
- Lecteur choisi par `Wines:Describer`. Sans clé avec `Claude` : un describer qui lève « Clé API Anthropic non configurée… ».
- **Écrans** :
  - `/wines` : liste par couleur, réordonnable, avec les statuts publié et brouillon, et un badge « Fiche IA à relire » ;
  - « Ajouter depuis la bibliothèque » : dialogue de recherche, puis prix ;
  - « Ajouter un vin » : formulaire manuel avec bouton « Rédiger la fiche avec l'assistant », qui remplit les champs, active le badge et exige la case « J'ai relu la fiche » avant publication ;
  - éditeur avec accords : sélection multiple de plats.

- [ ] **Step 1 : Tests (échec d'abord)** ; **Step 2 : implémenter** ; **Step 3 :** build et suite complète ; **Step 4 :** vérification manuelle en Demo ; **Step 5 : Commit** `feat: carte des vins (bibliothèque, fiches assistées, accords)`

---

### Task 5 : `ReservationService` et paiement

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/{ReservationService,PaymentService}.cs`, `src/ChefOS.Infrastructure/Payments/{IPaymentGateway,DemoPaymentGateway,MolliePaymentGateway,PaymentOptions}.cs`, `src/ChefOS.Infrastructure/Notifications/{IEmailSender,LoggingEmailSender}.cs`
- Modify : `DependencyInjection.cs` (`AddChefOsPayments(IConfiguration)`), `OrderService` (création d'une commande à partir d'une réservation, source `Vitrine`)
- Test : `ReservationServiceTests.cs`, `PaymentServiceTests.cs`, `MolliePaymentGatewayTests.cs` (faux `HttpMessageHandler` : corps JSON, en-têtes, montants au format `"10.00"`)

**Interfaces:**
- `IPaymentGateway` :
  - `Task<GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl, IDictionary<string,string> metadata, CancellationToken ct)` → `GatewayPayment(string Id, string CheckoutUrl)` ;
  - `Task<GatewayStatus> GetStatusAsync(string id, CancellationToken ct)` → `GatewayStatus { Open, Paid, Failed, Expired, Canceled }` ;
  - `Task RefundAsync(string id, decimal amount, CancellationToken ct)` ;
  - `string Name`.
- `DemoPaymentGateway` : paiements en mémoire (singleton, dictionnaire concurrent). `CheckoutUrl` = `{Vitrine base}/paiement-demo/{id}`. Méthode `SetStatus(id, status)` appelée par la page de démonstration.
- `MolliePaymentGateway` (HttpClient typé, base `https://api.mollie.com/v2/`) :
  - en-tête `Authorization: Bearer {key}` ;
  - création par `POST payments` avec `{ amount: { currency: "EUR", value: "10.00" }, description, redirectUrl, webhookUrl, metadata }` ;
  - statut par `GET payments/{id}` ;
  - remboursement par `POST payments/{id}/refunds` ;
  - erreurs traduites en français ;
  - la clé n'est jamais journalisée.
- `class ReservationService(IDbContextFactory<ChefOsDbContext> factory, TimeProvider clock, PaymentService payments, IEmailSender email)` :
  - `Task<IReadOnlyList<Slot>> GetSlotsAsync(DateOnly date, int partySize)` (via `AvailabilityCalculator`).
  - `Task<ReservationStart> CreateAsync(ReservationRequest request, string returnUrlBase, string webhookUrl)` :
    - Contrôles :
      - vitrine publiée et réservations ouvertes, sinon « Les réservations en ligne sont fermées » ;
      - créneau valide et disponible ;
      - taille du groupe entre 1 et le maximum ;
      - coordonnées : nom obligatoire, e-mail au format simple, téléphone de 6 caractères minimum ;
      - groupe au-delà du seuil : `Guests.Count == PartySize`, chaque convive a au moins un plat publié avec un prix (`ShowOnVitrine`) ; en dessous du seuil, les invités sont ignorés.
    - Anti-surréservation : dans une **transaction**, verrou consultatif PostgreSQL `pg_advisory_xact_lock(restaurantId, hash(service + date))`, puis recalcul du restant et insertion. Restant insuffisant : « Ce créneau vient d'être complété, choisissez-en un autre ».
    - Montant via `ReservationPricing.Compute`, prix des pré-commandes figés.
    - Montant 0 : statut `Confirmed` immédiatement.
    - Montant > 0 : statut `PendingPayment`, puis `PaymentService.StartAsync` crée le paiement chez la passerelle **après** le commit de la réservation. Si la passerelle échoue, la réservation passe en `Expired` et le message est traduit.
    - `PublicToken` aléatoire (32 octets, base64url).
    - E-mail de confirmation journalisé.
    - Renvoie `ReservationStart(string Token, string? CheckoutUrl, decimal AmountDue, ReservationStatus Status)`.
  - `Task<PublicReservation?> GetByTokenAsync(string token)` : relit le statut chez la passerelle si un paiement est `Open`.
  - `Task CancelByCustomerAsync(string token)` : `CancellationPolicy` gratuite → `Cancelled` et remboursement intégral automatique via `PaymentService.RefundAsync` ; sinon `Cancelled` sans remboursement. Refusé si la réservation est déjà installée ou passée.
  - **Back** :
    - `ListForDayAsync(DateOnly localDate)` : avec convives, plats et paiements, regroupés par service, couverts / capacité ;
    - `CreateManualAsync(...)` : sans paiement, `IsManual`, la capacité est vérifiée mais peut être dépassée avec `force: true` et un avertissement ;
    - `SeatAsync(id, expectedStatus)` : `Confirmed` → `Seated` ; crée une **commande** (`OrderService`, source `Vitrine`) avec les plats pré-commandés, la note de chaque ligne portant « Prénom Nom » ; `OrderId` est enregistré ;
    - `MarkNoShowAsync(id, expectedStatus)`, `CancelByRestaurantAsync(id, refund: bool)` ;
    - transitions conditionnelles (`expectedStatus`), comme pour les commandes.
- `class PaymentService(IDbContextFactory<ChefOsDbContext> factory, IPaymentGateway gateway, TimeProvider clock, ILogger<PaymentService> logger)` :
  - `StartAsync(reservationId, amount, kind, urls)` ;
  - `SyncAsync(string gatewayPaymentId)` : appelé par le webhook et le retour client, idempotent.
    - Paiement réussi et réservation `PendingPayment` encore valide : `Confirmed`.
    - Paiement réussi mais réservation `Expired` ou `Cancelled` : si la place est encore libre, `Confirmed` ; sinon remboursement automatique (`RefundPending` puis `Refunded`) et réservation laissée `Expired` avec une note.
    - Paiement en échec, expiré ou annulé : réservation `Expired`.
  - `RefundAsync(paymentId)` : idempotent, un seul remboursement.
- Expiration paresseuse : une réservation `PendingPayment` plus vieille que `PendingPaymentMinutes` est considérée expirée par les calculs. `ExpireStaleAsync()` (appelée au chargement des listes du back et des créneaux) écrit le statut `Expired`.

- [ ] **Step 1 : Tests (échec d'abord), dates fixes via `FakeTimeProvider`**
  - `Create_SmallGroup_DepositPendingThenConfirmedOnPaid` ;
  - `Create_LargeGroup_RequiresGuestsAndDishes_PrepaymentOnPreorder` ;
  - `Create_ZeroAmount_ConfirmedImmediately` ;
  - `Create_Concurrent_LastSeats_NoOverbooking` (Review Focus 1 : 6 créations parallèles sur 4 places restantes) ;
  - `Create_UnpublishedRestaurant_Refused` ;
  - `Create_ClosedDayOrPastSlot_Refused` ;
  - `Payment_FailedOrExpired_ReleasesSeat` ;
  - `Payment_PaidAfterExpiryAndSeatTaken_Refunded` (Review Focus 4) ;
  - `Payment_Sync_Idempotent` ;
  - `Cancel_FreeWindow_Refunded_LateNoRefund` ;
  - `Seat_CreatesOrderWithGuestNames` ;
  - `Seat_StaleStatus_Refused` ;
  - `Manual_OverCapacityNeedsForce` ;
  - `GetByToken_UnknownToken_Null` ;
  - `OtherRestaurant_Isolated` ;
  - tests Mollie (requête, statut, remboursement, traduction des erreurs).
- [ ] **Step 2 : Implémenter** · **Step 3 :** suite complète, aucun avertissement. **Step 4 : Commit** `feat: réservations avec acompte ou prépaiement (Demo et Mollie), annulation et arrivée en salle`

---

### Task 6 : Back — écran des réservations

**Files:**
- Create : `src/ChefOS.Back/Components/Pages/Reservations/{ReservationDay.razor, ReservationCard.razor, ManualReservationDialog.razor, ReservationDayView.cs}`
- Modify : `Dashboard.razor` (bloc « Réservations du jour » : couverts par service et prochain groupe), `DashboardService` si nécessaire
- Test : `tests/ChefOS.Tests/Back/ReservationDayViewTests.cs`

Écran `/reservations` :
- sélecteur de jour (aujourd'hui par défaut, **date locale du restaurant**) ;
- par service : couverts réservés / capacité et une barre de remplissage ;
- cartes : heure locale, nom, nombre de couverts, statut, téléphone, note, montant payé ou dû, convives et leurs plats ;
- actions selon le statut : « Arrivé » (crée la commande), « No-show », « Annuler » (avec choix de rembourser si un paiement existe), « Rembourser » (paiement `Paid` d'une réservation annulée), plus « Nouvelle réservation » (dialogue manuel) ;
- rafraîchissement automatique toutes les 60 s, comme pour les commandes, sans `_busy` ;
- refus d'un statut périmé : message, puis rechargement.

- [ ] Tests de la vue (regroupement par service, ordre, montants) → implémentation → build et tests → vérification manuelle → **Commit** `feat: écran des réservations du jour`

---

### Task 7 : Site public de la vitrine

**Files:**
- Modify : `src/ChefOS.Vitrine/Program.cs` (MudBlazor **non** utilisé : CSS sur mesure ; `AddInteractiveServerComponents` pour la réservation ; `SlugTenantContext` ; `AddChefOsPayments`, `AddChefOsWineDescriber` facultatif ; exécution de `WineLibrarySeeder` au démarrage ; rate limiter `fixed window` sur les envois de réservation, 10/min par IP), `App.razor`, layout
- Create : `src/ChefOS.Vitrine/Tenancy/SlugTenantContext.cs` (+ middleware ou composant qui le remplit depuis `{slug}`) ; pages `Pages/{RestaurantHome,WineLibrary,WineDetail,Reserve,ReservationStatus,DemoCheckout}.razor` ; `Pages/Reserve/ReserveForm.cs` (logique pure du formulaire) ; endpoints `GET /{slug}/images/{id}`, `POST /webhooks/mollie` ; `wwwroot/vitrine.css`
- Test : `tests/ChefOS.Tests/Vitrine/{SlugTenantContextTests,ReserveFormTests}.cs`, plus un test d'intégration `WebApplicationFactory<VitrineProgram>` : `Vitrine_Unpublished_Returns404`, `Vitrine_Published_ShowsMenuAndWines`, `Images_OtherRestaurant_404` (Review Focus 5)

Pages :
- **`/{slug}`** :
  - couverture plein écran (image ou dégradé dans la couleur d'accent), nom, ville, thème, texte d'ambiance, galerie ;
  - **menu** : catégories, nom, description publique, prix, icônes d'allergènes avec leur libellé au survol ou en texte ;
  - aperçu de la carte des vins et bouton « Toute la carte des vins » ;
  - section Réservation avec le bouton « Réserver une table », et un bouton flottant sur mobile ;
  - adresse et téléphone en pied de page.
- **`/{slug}/vins`** : filtres (couleur, région), cartes ; **`/{slug}/vins/{id}`** : fiche complète, accords avec les plats de la maison (liens vers le menu).
- **`/{slug}/reserver`** (composant Interactive Server) :
  1. date (calendrier des 60 prochains jours, jours fermés grisés), taille du groupe, créneaux disponibles ;
  2. si le groupe dépasse le seuil : une carte par convive (prénom, nom, ajout de plats du menu publié avec quantité, total en direct) ;
  3. coordonnées et remarque ; récapitulatif : montant à payer, nature (acompte ou prépaiement), **règle d'annulation affichée en clair**, case à cocher « J'accepte » ;
  4. « Réserver et payer » (ou « Réserver » si le montant est nul) → redirection vers le paiement → retour sur `/{slug}/reservation/{token}`.
- **`/{slug}/reservation/{token}`** : statut (relu chez la passerelle), récapitulatif, bouton « Annuler ma réservation » avec la règle de remboursement.
- **`/paiement-demo/{id}`** : uniquement en mode Demo, boutons « Payer » et « Simuler un échec ».
- Partout : HTML sémantique, mobile d'abord, contraste AA, `lang="fr-BE"`, titre et meta description par restaurant, aucune donnée d'un autre restaurant.

- [ ] Tests (échec d'abord) → implémentation → build et tests → **vérification manuelle complète en mode Demo** (voir Task 8, étape 4) → **Commit** `feat: site public — ambiance, menu, carte des vins et réservation`

---

### Task 8 : Données de démo, README, vérification finale

**Files:**
- Modify : `DemoSeeder` / `DemoActivity` (ou un nouveau `DemoVitrine.cs` appelé dans le même flux compensé), `DemoSeederTests`, `README.md`

- **Démo du Petit Bistro** :
  - vitrine **publiée** : thème, texte d'ambiance, couleur, pas d'image (dégradé), téléphone, ville Liège ;
  - services du mardi au samedi : Midi 12:00–14:00 (40 couverts) et Soir 19:00–22:00 (50 couverts) ; fermeture dimanche et lundi ; une fermeture exceptionnelle ;
  - descriptions publiques sur les 12 plats et `ShowOnVitrine` ;
  - 15 vins de la carte pris dans la bibliothèque, avec prix et 1 ou 2 accords chacun, dont un vin « fiche IA à relire » non publié ;
  - réservations des 7 prochains jours : 6 petites tables confirmées (acompte payé en Demo), 1 groupe de 8 confirmé avec convives nommés et plats pré-commandés, 1 en attente de paiement expirée, 1 annulée.
- **`DemoSeederTests`** : la vitrine est publiée, au moins 60 vins de bibliothèque et 15 sur la carte, le groupe de 8 a ses 8 convives avec plats, des créneaux restent disponibles cette semaine, et tout est idempotent.
- **README** : section « Vitrine et réservations ». Elle donne les URL (back 5268, vitrine 5108, `/le-petit-bistro`), explique les modes Demo et Mollie (`dotnet user-secrets set Mollie:ApiKey test_… --project src/ChefOS.Vitrine`, `Payments:Gateway=Mollie`, webhook non joignable en local, d'où la relecture au retour), les fiches IA (`Wines:Describer=Claude`), et le fait qu'aucun e-mail n'est envoyé.

- [ ] Tests du seed (échec d'abord) → seed → README → build sans avertissement et suite complète.
- [ ] **Step 4 : Vérification complète sur une base vide** (`docker compose down -v && up -d`, lancer le back puis la vitrine) :
  1. `/le-petit-bistro` affiche l'ambiance, le menu, les vins et la fiche d'un vin ;
  2. réservation pour 2 : acompte de 20 €, paiement Demo, statut Confirmée ;
  3. réservation pour 8 : 8 convives avec plats, prépaiement à 30 %, paiement ;
  4. annulation via le lien ;
  5. dans le back, `/reservations` : « Arrivé » sur le groupe crée la commande visible sur `/orders`.
  Arrêter les applications.
- [ ] **Step 5 : Commit** `feat: démo vitrine et réservations, README`
