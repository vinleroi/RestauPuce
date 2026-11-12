# Étape 5 — Prêt pour la production : plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal :** rendre ChefOS hébergeable pour de vrais restaurants : vrais e-mails fiables, journées et heures du back dans le fuseau du restaurant, déploiement Docker derrière HTTPS avec configuration vérifiée, et corrections reportées.

**Architecture :**
- **Fuseau.** Un objet valeur `RestaurantCalendar` (fuseau + instant figé) fait toutes les conversions jour/instant. Les services l'obtiennent par `RestaurantCalendar.ForCurrentRestaurantAsync(db, clock)`, et les pages par le service scoped `RestaurantTime`.
- **E-mails.**
  - Ils passent par une file en base (`OutgoingEmails`), écrite dans la transaction de l'événement.
  - Un `BackgroundService`, présent dans les deux sites, réserve les messages de façon atomique et les envoie par un `IEmailTransport` (MailKit ou journal).
- **Hébergement.**
  - Une validation de configuration au démarrage, hors `Development`.
  - En-têtes transférés, clés de protection des données en base, contrôles de santé.
  - Migrations appliquées par le back ; images Docker et Caddy.

**Tech Stack :** .NET 10, Blazor Web App, MudBlazor 9, EF Core 10 + Npgsql, PostgreSQL 17, MailKit, Testcontainers (PostgreSQL, Mailpit), xUnit, FakeTimeProvider.

**Spec :** `docs/superpowers/specs/2026-11-12-mise-en-production-design.md` (à lire en entier avant la première tâche).

## Global Constraints

- **Dépôt et branche.** Dépôt `~/Desktop/ChefOS`, branche `feature/etape-5-production`.
- **Commits.** Messages en français, préfixe conventionnel (`feat:`, `fix:`, `test:`, `docs:`, `chore:`), terminés par `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Ne jamais commiter `.superpowers/`.
- **Langue.** Interface, messages d'erreur, e-mails et commentaires de code en français (fr-BE), comme le reste du code.
- **Secrets.** Jamais dans un appsettings ni dans un fichier versionné. User-secrets en développement, variables d'environnement en production. Jamais journalisés.
- **Adresses dans les journaux.** Toujours masquées avec la fonction `Mask` existante (`j***@d***.be`). Le corps d'un e-mail n'est jamais écrit au niveau Information ou plus haut.
- **Aucun test n'appelle un service externe réel** (SMTP réel, Mollie, Anthropic). Mailpit et PostgreSQL tournent en conteneurs locaux.
- **Multi-restaurant.**
  - Tout ce qui appartient à un restaurant reste `ITenantScoped` et filtré.
  - `Restaurants` n'a pas de filtre : toute lecture filtre explicitement `r.Id == …`.
  - `OutgoingEmails` et les clés de protection des données sont globales et exposées par aucune page.
- **Aucun changement de règle métier** (prix, capacité, annulation, coûts), à part les corrections du § 6 de la spec.
- **Vérification de fin de tâche.**
  - Commande : `dotnet build ChefOS.sln -warnaserror` doit indiquer 0 avertissement.
  - Commande : `dotnet test ChefOS.sln` doit être entièrement verte. Docker doit tourner, car les tests utilisent Testcontainers.
- **Taille des fichiers.** Aucun nouveau fichier de plus de ~400 lignes. Un fichier existant qui dépasse 600 lignes à cause de la tâche voit sa nouvelle partie extraite.
- **Migrations EF.** Chaque tâche qui change le modèle ajoute sa propre migration :
  `dotnet ef migrations add <Nom> --project src/ChefOS.Infrastructure --startup-project src/ChefOS.Back`.

## Review Focus

1. **Jour du changement d'heure** (23 h ou 25 h). Une commande à 02:30 la nuit du passage à l'heure d'hiver compte une seule fois, et pour le bon jour. Les bornes d'un jour local couvrent exactement ses heures réelles. *(Tests ajoutés à la tâche 1.)*
2. **Serveur SMTP qui ne répond pas.** L'envoi d'un message est borné à 30 s. Un dépassement compte comme une panne du serveur : le passage s'arrête sans compter d'essai et reprend 30 s plus tard. L'arrêt du site n'attend pas indéfiniment. *(Test ajouté à la tâche 3.)*
3. **Saisie hostile dans un e-mail.** Un nom de client ou de restaurant contenant `<script>` ou un retour à la ligne : le HTML est échappé, et le sujet ne contient aucun caractère de contrôle (pas d'injection d'en-tête). *(Test ajouté à la tâche 4.)*
4. **Restaurant sans adresse, téléphone ni e-mail publics.** L'e-mail omet ces lignes, sans « null » ni ligne vide orpheline, et n'a pas de `Reply-To`. *(Test ajouté à la tâche 4.)*
5. **Base indisponible au démarrage de la vitrine.** Le site démarre quand même, `/healthz` répond 200 et `/healthz/ready` répond 503 jusqu'à ce que le schéma existe. *(Test ajouté à la tâche 12.)*

---

### Task 1 : Calendrier du restaurant dans les services

Spec : § 4.1, § 4.2 (services), § 4.3.

**Files :**
- Create : `src/ChefOS.Infrastructure/Services/RestaurantCalendar.cs`
- Create : `src/ChefOS.Infrastructure/Services/RestaurantTime.cs`
- Modify :
  - `src/ChefOS.Infrastructure/Data/ChefOsDbContext.cs` (méthode `CurrentTimeZoneIdAsync`)
  - `src/ChefOS.Infrastructure/Services/UtcDays.cs` (retirer `IsInTheFuture`, garder `Of` pour normaliser les étiquettes de jour)
  - `OrderService.cs`, `StatsService.cs`, `DashboardService.cs`, `SalesService.cs`, `WasteService.cs`, `InventoryService.cs`, `ReceiptService.cs`, `IngredientService.cs`, `RestaurantService.cs`
  - `src/ChefOS.Infrastructure/DependencyInjection.cs` (enregistrer `RestaurantTime`)
- Test :
  - `tests/ChefOS.Tests/Infrastructure/RestaurantCalendarTests.cs` (unitaires)
  - `tests/ChefOS.Tests/Infrastructure/RestaurantDaysTests.cs` (services contre PostgreSQL)

**Interfaces :**
- Produces :
  ```csharp
  public sealed class RestaurantCalendar
  {
      public RestaurantCalendar(string timeZoneId, DateTime utcNow);
      public string TimeZoneId { get; }
      public DateTime UtcNow { get; }                                  // Kind = Utc
      public DateTime Today { get; }                                   // étiquette : date locale à 00:00, Kind = Utc
      public DateTime DayOf(DateTime instantUtc);                      // étiquette du jour local d'un instant
      public (DateTime FromUtc, DateTime ToUtc) UtcBounds(DateTime day); // [00:00 local, 00:00 local du lendemain[ en UTC
      public DateTime ToLocal(DateTime instantUtc);                    // heure locale, Kind = Unspecified (affichage)
      public bool IsInTheFuture(DateTime day);                         // day.Date > Today + 1 jour
      public static Task<RestaurantCalendar> ForCurrentRestaurantAsync(ChefOsDbContext db, TimeProvider clock, CancellationToken ct = default);
  }
  public sealed class RestaurantTime(IDbContextFactory<ChefOsDbContext> factory, TimeProvider clock)
  {
      public Task<RestaurantCalendar> GetAsync(CancellationToken ct = default); // mis en cache pour le scope, l'instant est relu à chaque appel
  }
  // Sur ChefOsDbContext : fuseau du restaurant courant, « Europe/Brussels » sans restaurant.
  public Task<string> CurrentTimeZoneIdAsync(CancellationToken ct = default);
  ```
- **Horloge des services.** Chaque service listé reçoit un paramètre de constructeur optionnel `TimeProvider? clock = null`, en dernier ou juste après la fabrique selon les signatures existantes ; `TimeProvider.System` par défaut. Le conteneur injecte le `TimeProvider` enregistré. Les tests existants qui font `new XService(factory)` compilent toujours.
- **Signatures inchangées.** `OrderService.ListForDayAsync(DateTime day)` (le paramètre est une étiquette de jour) et `StatsService.GetAsync(DateTime fromDay, DateTime toDay)`.

- [ ] **Step 1 : écrire les tests unitaires de `RestaurantCalendar`.**
  - `Today_IsTheLocalDate` : `new RestaurantCalendar("Europe/Brussels", 2026-11-12T23:30Z).Today == 2026-11-13T00:00 (Utc)`.
  - `DayOf_ConvertsTheInstantToTheLocalDay` : `DayOf(2026-11-12T22:59Z) == 2026-11-12`, et `DayOf(2026-11-12T23:00Z) == 2026-11-13`.
  - `UtcBounds_CoverTheShortSpringDay` : `UtcBounds(2026-03-29)` vaut `(2026-03-28T23:00Z, 2026-03-29T22:00Z)`.
  - `UtcBounds_CoverTheLongAutumnDay` : `UtcBounds(2026-10-25)` vaut `(2026-10-24T22:00Z, 2026-10-25T23:00Z)`.
  - `DayOf_TheRepeatedAutumnHour_StaysOnTheSameDay` : `DayOf(2026-10-25T00:30Z)` et `DayOf(2026-10-25T01:30Z)` valent tous deux `2026-10-25`.
  - `Auckland_HasItsOwnToday` : à `2026-11-12T11:30Z`, `Today` vaut `2026-11-13` à Auckland et `2026-11-12` à Bruxelles.
  - `IsInTheFuture_AllowsLocalTomorrow` : à `2026-11-12T23:30Z` à Bruxelles, `IsInTheFuture(2026-11-14)` est faux et `IsInTheFuture(2026-11-15)` est vrai.
  - `ToLocal_ReturnsUnspecifiedLocalTime` : `ToLocal(2026-07-01T10:00Z)` vaut `2026-07-01T12:00`, `Kind == Unspecified`.
- [ ] **Step 2 : lancer les tests et vérifier qu'ils échouent.**
  Commande : `dotnet test --filter RestaurantCalendarTests`. Attendu : la compilation échoue, `RestaurantCalendar` n'existe pas.
- [ ] **Step 3 : implémenter `RestaurantCalendar` et `CurrentTimeZoneIdAsync`.** S'appuyer sur `RestaurantClock` (`ChefOS.Core.Vitrine`) pour les conversions ; ne pas dupliquer ses règles d'heure inexistante ou ambiguë.
- [ ] **Step 4 : relancer les tests.** Commande : `dotnet test --filter RestaurantCalendarTests`. Attendu : PASS.
- [ ] **Step 5 : écrire les tests de services `RestaurantDaysTests`** (collection `postgres`, `FakeTimeProvider` réglé sur `2026-11-12T23:30Z`, restaurant Bruxelles créé par `TestData`).
  - `OrderCreatedAfterLocalMidnight_IsListedOnTheLocalDay`.
    - Une commande servie, créée à `2026-11-12T23:30Z`, apparaît dans `ListForDayAsync(2026-11-13)`, et pas dans `ListForDayAsync(2026-11-12)`.
    - Utiliser une commande servie : les commandes ouvertes de la veille restent visibles par règle.
  - `StatsCountOrderLinesOnTheLocalDay` : la même commande (1 plat × 20 € TTC, TVA 12 %) donne un chiffre d'affaires HT de 17,86 € (arrondi au centime à l'assertion) dans `GetAsync(2026-11-13, 2026-11-13)`, et 0 dans `GetAsync(2026-11-12, 2026-11-12)`.
  - `DashboardLastSevenDays_EndsOnLocalToday` : `GetLastSevenDaysAsync()` renvoie `From = 2026-11-07`, `To = 2026-11-13`.
  - `WasteDatedLocalTomorrow_IsAccepted_DayAfterIsRefused` : une perte datée du 14/11 est acceptée ; une perte datée du 15/11 lève `DomainValidationException`.
  - `AutumnNightOrder_CountsOnce` : une commande à `2026-10-25T01:30Z` (02:30 locale de la seconde occurrence) compte une seule fois, et pour le 25/10.
  - `AucklandRestaurant_KeepsItsOwnDays` : dans la même base, un restaurant `Pacific/Auckland` voit la commande de `2026-11-12T11:30Z` au 13/11, et le restaurant de Bruxelles voit la sienne au 12/11.
- [ ] **Step 6 : lancer ces tests et vérifier qu'ils échouent.** Commande : `dotnet test --filter RestaurantDaysTests`. Attendu : FAIL sur les jours.
- [ ] **Step 7 : brancher les services sur le calendrier.**
  - `UtcDays.IsInTheFuture` devient `calendar.IsInTheFuture`.
  - Le jour d'un instant (`UtcDays.Of(l.CreatedAt)`, etc.) devient `calendar.DayOf`.
  - Les intervalles d'instants d'un jour deviennent `calendar.UtcBounds`.
  - Les `DateTime.UtcNow` deviennent `clock.GetUtcNow().UtcDateTime`.
  - `UtcDays.Of` reste pour normaliser les étiquettes saisies (`SalesEntry.Date`, etc.).
- [ ] **Step 8 : lancer toute la suite.** Commandes : `dotnet build ChefOS.sln -warnaserror && dotnet test ChefOS.sln`. Attendu : 0 avertissement, tout est vert. Adapter les tests existants qui supposaient les jours UTC, en le justifiant dans le message de commit.
- [ ] **Step 9 : commit.** `feat: journées du back dans le fuseau du restaurant (services)`

---

### Task 2 : Pages du back et démo sur l'heure du restaurant

Spec : § 4.2 (affichage, ventes, pertes, inventaires, démo).

**Files :**
- Modify, pages du back (`src/ChefOS.Back/Components/Pages/…`) :
  - `Dashboard.razor`
  - `Orders/OrderBoard.razor`
  - `Sales/SalesWeekPage.razor`
  - `Stats/StatsPage.razor`
  - `Waste/WasteList.razor`
  - `Waste/WasteDialog.razor`
  - `Inventory/InventoryList.razor`
  - `Receipts/ReceiptList.razor`
  - `Receipts/ReceiptReview.razor`
  - `Ingredients/IngredientDetail.razor`
- Modify, démo :
  - `src/ChefOS.Infrastructure/Seeding/DemoSeeder.cs` (paramètre `TimeProvider clock`)
  - `DemoActivity.cs`
  - `DemoVitrine.cs` (si besoin)
- Create : `tests/ChefOS.Tests/Back/NoServerClockTests.cs`
- Modify : `tests/ChefOS.Tests/Infrastructure/DemoSeederTests.cs`

**Interfaces :**
- Consumes : `RestaurantTime.GetAsync()` et `RestaurantCalendar` (Task 1).
- Produces : `DemoSeeder(UserManager<ApplicationUser> users, DbContextOptions<ChefOsDbContext> options, IOptions<DemoOptions> demo, TimeProvider clock)`.

- [ ] **Step 1 : écrire `NoServerClockTests.SourcesNeverReadTheServerClock`.**
  - Le test remonte jusqu'au dossier qui contient `ChefOS.Sln`.
  - Il parcourt les `*.cs` et `*.razor` de `src/ChefOS.Back`, `src/ChefOS.Infrastructure/Services` et `src/ChefOS.Infrastructure/Seeding`, hors `bin`, `obj` et `Migrations`.
  - Il échoue en listant fichier:ligne pour chaque occurrence de `DateTime.UtcNow`, `DateTime.Now`, `DateTime.Today`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow` ou `.ToLocalTime()`.
- [ ] **Step 2 : écrire le test du seed** `DemoSeederTests.Seed_UsesTheInjectedClock`.
  - `FakeTimeProvider` à `2026-11-12T23:30Z`.
  - La commande de démonstration la plus récente tombe le 13/11, jour local de Bruxelles.
  - Aucune donnée n'est postérieure à l'instant fixé.
  - Adapter les tests existants du seed pour passer l'horloge fixe.
- [ ] **Step 3 : lancer les tests et vérifier qu'ils échouent.** Commande : `dotnet test --filter "NoServerClockTests|DemoSeederTests"`. Attendu : FAIL, avec la liste des occurrences actuelles.
- [ ] **Step 4 : remplacer chaque occurrence.**
  - **Pages.** `@inject RestaurantTime` ; le calendrier est chargé dans `OnInitializedAsync`.
    - Dates par défaut et maximales : `calendar.Today` et `calendar.Today.AddDays(1)`.
    - `StatsPeriod.Of(preset, calendar.Today)`.
    - Affichages : `calendar.ToLocal(instant)`.
  - **Historique des prix.** Une entrée dont la source est le ticket s'affiche à `p.Date` sans conversion ; les autres passent par `calendar.ToLocal(p.Date)`. Si le DTO d'historique ne porte pas la source, l'ajouter.
  - **Seed.** Il prend `clock.GetUtcNow()` ; les jours de démonstration partent de `RestaurantClock.Today(now, "Europe/Brussels")`.
- [ ] **Step 5 : relancer les deux tests puis la suite complète.** Attendu : PASS, 0 avertissement.
- [ ] **Step 6 : vérifier dans le navigateur.**
  - Lancer le back avec `TZ=UTC dotnet run --project src/ChefOS.Back --launch-profile http`.
  - Ouvrir le tableau des commandes et le tableau de bord : « actualisé à » affiche l'heure de Bruxelles.
- [ ] **Step 7 : commit.** `feat: pages du back et démo à l'heure du restaurant`

---

### Task 3 : File d'e-mails et envoi SMTP

Spec : § 3.1, § 3.2.

**Files :**
- Create, dans `src/ChefOS.Infrastructure/Notifications/` :
  - `OutgoingEmail.cs` (entité, `OutgoingEmailStatus { Pending, Sent, Abandoned }`, `EmailKind`)
  - `EmailMessage.cs`
  - `EmailOutbox.cs`
  - `EmailDispatchSignal.cs`
  - `IEmailTransport.cs` (et `EmailTransportException`)
  - `SmtpEmailTransport.cs`
  - `LoggingEmailTransport.cs` (renomme `LoggingEmailSender`, garde `Mask`)
  - `OutgoingEmailDispatcher.cs`
  - `EmailOptions.cs`
- Create : `src/ChefOS.Infrastructure/Data/Configurations/OutgoingEmailConfiguration.cs`, plus la migration `AddOutgoingEmails`.
- Delete : `IEmailSender.cs` et `LoggingEmailSender.cs`. Leurs usages migrent à la tâche 4 ; d'ici là, `ReservationService` et `PaymentService` reçoivent l'outbox (voir Step 6).
- Modify :
  - `DependencyInjection.cs` : `AddChefOsEmail`, et retrait du `TryAddSingleton<IEmailSender, …>` de `AddChefOsPayments`.
  - `src/ChefOS.Back/Program.cs`, `src/ChefOS.Vitrine/Program.cs` : appeler `AddChefOsEmail`.
  - `docker-compose.yml` : service `mailpit`.
  - Les deux `appsettings.Development.json`.
  - `src/ChefOS.Infrastructure/ChefOS.Infrastructure.csproj` : MailKit.
  - `tests/ChefOS.Tests/Vitrine/VitrineSite.cs` : `Email:Sender = Log`.
- Test :
  - `tests/ChefOS.Tests/Infrastructure/OutgoingEmailDispatcherTests.cs`
  - `tests/ChefOS.Tests/Infrastructure/SmtpEmailTransportTests.cs`
  - `tests/ChefOS.Tests/Infrastructure/MailpitFixture.cs`

**Interfaces :**
- Produces :
  ```csharp
  public enum EmailKind { ReservationConfirmed, ReservationCancelledByCustomer, ReservationCancelledByRestaurant, LatePaymentRefunded, AccountConfirmation, PasswordReset }
  public sealed record EmailMessage(EmailKind Kind, string To, string Subject, string TextBody, string HtmlBody, string? ReplyTo = null, int? RestaurantId = null);
  public static class EmailOutbox
  {
      // Ajoute la ligne au contexte, sans SaveChanges : l'appelant enregistre dans sa propre transaction.
      public static void Enqueue(ChefOsDbContext db, EmailMessage message, DateTime nowUtc);
  }
  public sealed class EmailDispatchSignal { public void Notify(); public Task WaitAsync(TimeSpan timeout, CancellationToken ct); }
  public interface IEmailTransport { Task SendAsync(EmailMessage message, CancellationToken ct); }
  public enum EmailFailure { ServerUnavailable, Temporary, Permanent }
  public sealed class EmailTransportException(string reason, EmailFailure failure, Exception? inner = null) : Exception(reason, inner) { public EmailFailure Failure { get; } }
  public sealed class OutgoingEmailDispatcher : BackgroundService
  {
      public OutgoingEmailDispatcher(IDbContextFactory<ChefOsDbContext> factory, IEmailTransport transport, EmailDispatchSignal signal, TimeProvider clock, ILogger<OutgoingEmailDispatcher> logger);
      public Task<int> DispatchDueAsync(CancellationToken ct); // un passage : réserve, envoie et marque ; renvoie le nombre de messages traités
      public Task<int> PurgeAsync(CancellationToken ct);       // supprime les lignes de plus de 30 jours ; renvoie le nombre supprimé
  }
  public static IServiceCollection AddChefOsEmail(this IServiceCollection services, IConfiguration configuration);
  ```
- **`DbSet<OutgoingEmail> OutgoingEmails`** sur `ChefOsDbContext`, sans filtre de tenant.
- **Colonnes :**
  - `Id`, `Kind`, `RestaurantId?`, `To` (320), `ReplyTo?` (320), `Subject` (300) ;
  - `TextBody?`, `HtmlBody?` (effacés après envoi ou abandon) ;
  - `Status`, `Attempts`, `CreatedAt`, `NextAttemptAt`, `LockedUntil?`, `SentAt?` ;
  - `LastError?` (500, sans adresse ni corps).
  - Index sur (`Status`, `NextAttemptAt`).
- **Valeurs fixées par la spec :**
  - délais après chaque échec `Temporary` : `[1 min, 5 min, 30 min, 2 h, 6 h]`, puis abandon (6 essais au total) ;
  - `Permanent` : abandon immédiat ;
  - `ServerUnavailable` : le passage s'arrête, le message en cours est remis tel quel (`Attempts` inchangé, bail levé), et le passage suivant attend 30 s ;
  - un message créé il y a plus de 24 h est abandonné sans envoi ;
  - réveil toutes les 5 s, ou sur `Notify()` ;
  - suppression au bout de 30 jours ;
  - délai d'un envoi : 30 s ;
  - bail de réservation : 2 min (un message réservé par un processus arrêté repart après expiration du bail).
- **Configuration (`EmailOptions`).**
  - `Sender` : `Log` par défaut, `Smtp`.
  - `FromAddress`, `FromName` (« ChefOS »).
  - `Smtp:Host`, `Smtp:Port` (587), `Smtp:Security` (`StartTls`, `SslOnConnect` ou `None`), `Smtp:Username`, `Smtp:Password`.
- **Classement des erreurs (`SmtpEmailTransport`).**
  - `ServerUnavailable` : connexion impossible, TLS ou authentification refusés, délai dépassé.
  - `Temporary` : une réponse SMTP 4xx sur ce message.
  - `Permanent` : une réponse 5xx sur ce message (dont un destinataire refusé).

- [ ] **Step 1 : écrire `OutgoingEmailDispatcherTests`** (PostgreSQL, `FakeTimeProvider`, faux transport en mémoire).
  - `DueMessage_IsSentOnce_AndItsBodyIsCleared` : après `DispatchDueAsync`, `Status = Sent`, `SentAt` est renseigné, `TextBody` et `HtmlBody` sont null, et le transport a reçu 1 message.
  - `TemporaryFailure_RetriesAfterTheScheduledDelays` : le transport échoue toujours (`Temporary`). Après chaque passage, on avance l'horloge du délai attendu ; `NextAttemptAt - now` vaut successivement 1, 5, 30, 120 et 360 min, puis `Abandoned` au 6e échec, et `Attempts == 6`.
  - `PermanentFailure_AbandonsImmediately` : `Attempts == 1`, `Status = Abandoned`, le corps est effacé, `LastError` est renseigné.
  - `ServerUnavailable_StopsThePassWithoutCountingAnAttempt` : 3 messages dus. Le transport n'est appelé qu'une fois ; les 3 messages restent `Pending` avec `Attempts == 0`.
  - `ServerBack_SendsTheQueueOnTheNextPass` : après la panne, le transport répond de nouveau, et le passage suivant envoie les 3 messages.
  - `MessageOlderThan24Hours_IsAbandonedWithoutSending`.
  - `MessageNotYetDue_IsLeftAlone`.
  - `TwoDispatchers_NeverSendTheSameMessageTwice` : 20 messages, deux dispatchers sur la même base lancés en parallèle (`Task.WhenAll`), le transport compte les envois par Id. Chaque Id est envoyé exactement une fois.
  - `ExpiredLease_IsPickedUpAgain` : une ligne réservée (`LockedUntil` passé de 1 s) est renvoyée.
  - `HangingTransport_TimesOutAsServerUnavailable` *(Review Focus 2)* : un transport qui attend l'annulation. Le passage se termine en moins de 35 s, le message reste `Pending` avec `Attempts == 0`, et un `StopAsync` du dispatcher rend la main en moins de 5 s.
  - `Purge_DeletesRowsOlderThan30Days` : les lignes de 31 jours sont supprimées, celles de 29 jours restent.
- [ ] **Step 2 : écrire `SmtpEmailTransportTests`.**
  - `MailpitFixture` : `ContainerBuilder` de Testcontainers, image `axllent/mailpit` à une version précise et existante (la vérifier avec `docker pull`), ports 1025 et 8025. `MP_SMTP_ALLOWED_RECIPIENTS` n'accepte que `@client.test`.
  - `Send_DeliversTextHtmlAndReplyTo` : lu via `GET /api/v1/messages` puis `GET /api/v1/message/{ID}`. Sujet, `Reply-To`, partie texte et partie HTML sont présents.
  - `RejectedRecipient_IsPermanent` : destinataire `x@ailleurs.test`, on attend `EmailTransportException { Failure: Permanent }`.
  - `UnreachableServer_IsServerUnavailable` : port fermé, on attend `Failure == ServerUnavailable`.
- [ ] **Step 3 : lancer les tests et vérifier qu'ils échouent.** Commande : `dotnet test --filter "OutgoingEmailDispatcherTests|SmtpEmailTransportTests"`. Attendu : la compilation échoue.
- [ ] **Step 4 : implémenter.**
  - Entité, configuration et migration `AddOutgoingEmails`.
  - Réservation atomique au choix : `FOR UPDATE SKIP LOCKED` ou mise à jour conditionnelle du bail.
  - Transports.
  - Dispatcher : boucle `WaitAsync(5 s)` ; purge au plus une fois par heure ; une exception de boucle est journalisée et la boucle continue.
  - `AddChefOsEmail` : options, `EmailDispatchSignal` en singleton, transport selon `Sender`, `AddHostedService<OutgoingEmailDispatcher>`.
- [ ] **Step 5 : configuration de développement.**
  - `docker-compose.yml` : service `mailpit` sur `127.0.0.1:1025` et `127.0.0.1:8025`.
  - Les deux `appsettings.Development.json` : `"Email": { "Sender": "Smtp", "FromAddress": "reservations@chefos.local", "Smtp": { "Host": "localhost", "Port": 1025, "Security": "None" } }`.
  - `VitrineSite` force `Email:Sender = Log`.
- [ ] **Step 6 : faire compiler les appelants actuels.**
  - `ReservationService` et `PaymentService` ne prennent plus `IEmailSender`.
  - Leurs envois existants (`TrySendAsync`, `TrySendConfirmationAsync`) deviennent provisoirement `EmailOutbox.Enqueue` d'un `EmailMessage` texte simple, après le commit. La tâche 4 les remplace par les vrais gabarits, dans la transaction.
- [ ] **Step 7 : lancer toute la suite.** Attendu : verte, 0 avertissement.
- [ ] **Step 8 : vérifier à la main.**
  - `docker compose up -d mailpit` ; lancer la vitrine.
  - Faire une réservation sans paiement sur `/le-petit-bistro/reserver`.
  - Le message apparaît sur http://localhost:8025.
- [ ] **Step 9 : commit.** `feat: file d'e-mails en base et envoi SMTP (MailKit, Mailpit en dev)`

---

### Task 4 : E-mails des réservations

Spec : § 3.1 (format, Reply-To), § 3.3 (tableau des quatre e-mails), § 5.3 (`Vitrine:BaseUrl`).

**Files :**
- Create :
  - `src/ChefOS.Infrastructure/Notifications/EmailLayout.cs` (gabarit commun, texte et HTML)
  - `src/ChefOS.Infrastructure/Notifications/ReservationEmails.cs`
  - `src/ChefOS.Infrastructure/PublicSiteOptions.cs`
- Modify :
  - `ReservationService.cs` (`StartAsync`, `CreateManualAsync`, `CancelAsync`)
  - `PaymentService.cs` (`ApplyPaidAsync`)
  - `PaymentOptions.cs` (retirer `BaseUrl`)
  - `DependencyInjection.cs`
  - `src/ChefOS.Vitrine/Support/PublicUrls.cs`
  - `src/ChefOS.Back/Components/Pages/Vitrine/VitrineSettings.razor` (lire `PublicSiteOptions`)
  - les deux `appsettings.Development.json` (`Vitrine:BaseUrl`)
  - les tests qui posaient `Payments:BaseUrl` : `VitrineSite.cs`, `SupportTests.cs`, `MolliePaymentGatewayTests.cs`
  - `src/ChefOS.Vitrine/Tenancy/SlugTenantMiddleware.cs` et `SlugResolver.cs` (page de réservation retrouvée par le jeton)
- Test :
  - `tests/ChefOS.Tests/Infrastructure/ReservationEmailsTests.cs` (unitaires)
  - `tests/ChefOS.Tests/Infrastructure/ReservationEmailOutboxTests.cs` (services contre PostgreSQL)
  - `tests/ChefOS.Tests/Vitrine/VitrineSiteTests.cs` (lien de réservation)

**Interfaces :**
- Consumes : `EmailOutbox.Enqueue`, `EmailMessage`, `EmailKind`, `EmailDispatchSignal` (Task 3).
- Produces :
  ```csharp
  public sealed class PublicSiteOptions { public const string SectionName = "Vitrine"; public string BaseUrl { get; set; } = "http://localhost:5108"; }
  public sealed record ReservationEmailContext(
      string RestaurantName, string? AccentColor, string? PublicEmail, string? Phone, string? Address, string Slug, string BaseUrl,
      string CustomerName, string CustomerEmail, string Token, DateOnly LocalDate, TimeOnly LocalTime, string ServiceName, int PartySize,
      decimal AmountPaid, decimal RefundAmount, DateTime FreeCancellationUntilLocal, int RestaurantId);
  public static class ReservationEmails
  {
      public static EmailMessage Confirmed(ReservationEmailContext c);
      public static EmailMessage CancelledByCustomer(ReservationEmailContext c);
      public static EmailMessage CancelledByRestaurant(ReservationEmailContext c);
      public static EmailMessage LatePaymentRefunded(ReservationEmailContext c);
      public static string ReservationUrl(ReservationEmailContext c); // {BaseUrl sans / final}/{slug échappé}/reservation/{jeton}
  }
  public static class EmailLayout
  {
      public static (string Text, string Html) Render(string title, string? accentColor, IReadOnlyList<string> paragraphs, (string Label, string Url)? button);
      public static string CleanSubject(string subject); // retire CR, LF et autres caractères de contrôle
  }
  ```
- **Constructeur de `ReservationService`.** Le paramètre `IEmailSender email` est remplacé par `PublicSiteOptions site` ; on ajoute `EmailDispatchSignal? signal = null`.
- **Constructeur de `PaymentService`.** `IEmailSender? email = null` est remplacé par `PublicSiteOptions? site = null, EmailDispatchSignal? signal = null`. Sans `site`, on prend `new PublicSiteOptions()`.
- **Enregistrement.** `PublicSiteOptions` est lié à la section `Vitrine` dans `AddChefOsInfrastructure` et injecté en `IOptions<PublicSiteOptions>`. Les services reçoivent `.Value` via une fabrique.
- **Couleur d'accent.** Seule une valeur `#RRGGBB` est utilisée ; sinon on prend la couleur par défaut de la vitrine.
- **Sujets** (copie fixée) :
  1. `Votre réservation chez {Restaurant} est confirmée`
  2. `Votre réservation chez {Restaurant} est annulée`
  3. `{Restaurant} a annulé votre réservation`
  4. `Votre paiement à {Restaurant} a été remboursé`

- [ ] **Step 1 : écrire `ReservationEmailsTests`.**
  - `Confirmed_ContainsTheLocalDateTimeLinkAndDeadline` : 13/11/2026, 19:30, « Soir », 4 couverts, montant réglé « 40,00 € », lien `https://resto.test/le-petit-bistro/reservation/abc`, date limite « 12/11/2026 à 19:30 ».
  - `ReplyTo_IsThePublicEmail_WhenPublished`.
  - `MissingContactDetails_AreOmitted` *(Review Focus 4)* : pas de « null » dans le texte ni dans le HTML, pas de ligne « Téléphone : », `ReplyTo == null`.
  - `HostileInput_IsEscapedAndSubjectHasNoControlCharacters` *(Review Focus 3)* : avec le nom client `<script>alert(1)</script>` et le nom de restaurant `"Chez\r\nBcc: x@y"`, le HTML ne contient pas `<script>`, et le sujet ne contient ni `\r` ni `\n`.
  - `ReservationUrl_HandlesTrailingSlashAndEscapesTheSlug` : base `https://resto.test/` et slug `café` donnent `https://resto.test/caf%C3%A9/reservation/abc`.
  - `CancelledByCustomer_StatesTheRefund`, de deux façons : avec remboursement de 40 €, « 40,00 € » apparaît ; sans remboursement, « sans remboursement ».
- [ ] **Step 2 : écrire `ReservationEmailOutboxTests`** (PostgreSQL, passerelle Demo, `FakeTimeProvider`).
  - `OnlineBookingWithoutPayment_EnqueuesOneConfirmation`.
  - `FullSlot_EnqueuesNothing` : la réservation est refusée, et `OutgoingEmails` reste vide (transaction annulée).
  - `PaymentConfirmedTwice_EnqueuesOneConfirmation` : `SyncAsync` appelé deux fois.
  - `LatePaymentWithSlotTaken_EnqueuesLatePaymentRefunded`.
  - `CustomerCancelsInTime_EnqueuesCancellationWithRefundAmount`.
  - `RestaurantCancelsWithRefund_EnqueuesCancelledByRestaurant`.
  - `ManualBookingWithEmail_EnqueuesConfirmation_WithoutEmail_EnqueuesNothing`.
  - `EmailsCarryTheRestaurantIdAndTheReplyTo`.
- [ ] **Step 2 bis : écrire les tests du lien** dans `VitrineSiteTests` (spec, § 3.3, « Le lien fonctionne toujours »).
  - `ReservationLink_WorksWhenTheVitrineIsUnpublished` : réservation du restaurant `Hidden`, `GET /maison-cachee/reservation/{jeton}` répond 200 et affiche la réservation.
  - `ReservationLink_WithAnOldSlug_RedirectsToTheCurrentSlug` : `GET /ancien-slug/reservation/{jeton}` répond 302 vers `/le-bistro-test/reservation/{jeton}`.
  - `ReservationLink_WithAnUnknownToken_Returns404`.
  - `ReservationLink_OfAnotherRestaurant_RedirectsToItsOwnSlug` : le jeton d'`Other` sous le slug de `Bistro` redirige vers le slug d'`Other` ; jamais les données d'`Other` sous le slug de `Bistro`.
  - `OtherPages_OfAnUnpublishedVitrine_StillReturn404` : `/maison-cachee` et `/maison-cachee/reserver` répondent toujours 404.
  - `Cancel_FromTheReservationPageOfAnUnpublishedVitrine_Works` : appeler `ReservationService.CancelByCustomerAsync` dans le scope de la requête, de sorte que le restaurant courant soit celui du jeton.
- [ ] **Step 3 : lancer les tests et vérifier qu'ils échouent.** Commande : `dotnet test --filter "ReservationEmails|ReservationLink|Cancel_FromTheReservationPage"`. Attendu : FAIL.
- [ ] **Step 4 : implémenter.**
  - Gabarits.
  - `PublicSiteOptions` remplace `PaymentOptions.BaseUrl` partout (`PublicUrls`, `DemoPaymentGateway`, page `VitrineSettings`).
  - Mise en file **avant le `CommitAsync`** de la transaction de l'événement : création, confirmation de paiement (dans le bloc de `ApplyPaidAsync`), remboursement d'un paiement tardif, annulation (montants pris avant le passage en `RefundPending`), réservation manuelle.
  - `signal?.Notify()` après le commit.
  - Retirer les envois provisoires de la tâche 3.
  - **Résolution du restaurant pour `/{slug}/reservation/{token}`.**
    - Le middleware de slug retrouve le restaurant par le jeton (lecture sans filtre de tenant de `Reservations.PublicToken`, puis le `Restaurant` correspondant). Il ignore la publication.
    - Il redirige vers le slug courant quand celui du lien diffère.
    - Toutes les autres routes gardent la règle « vitrine publiée, sinon 404 ».

- [ ] **Step 5 : lancer toute la suite.** Attendu : verte, 0 avertissement.
- [ ] **Step 6 : vérifier à la main avec Mailpit.**
  - Réserver, ouvrir l'e-mail, suivre le lien, annuler : l'e-mail d'annulation arrive.
  - Arrêter Mailpit, réserver, relancer Mailpit : l'e-mail arrive dans la minute.
- [ ] **Step 7 : commit.** `feat: e-mails de confirmation, d'annulation et de remboursement des réservations`

---

### Task 5 : E-mails des comptes et confirmation obligatoire

Spec : § 3.3 (comptes du back).

**Files :**
- Create :
  - `src/ChefOS.Back/Components/Account/IdentityOutboxEmailSender.cs`
  - `src/ChefOS.Back/Components/Account/RegistrationPolicy.cs`
- Delete : `src/ChefOS.Back/Components/Account/IdentityNoOpEmailSender.cs`
- Modify :
  - `src/ChefOS.Back/Program.cs` (options `Accounts:RequireConfirmedEmail` et `Accounts:AllowRegistration`, enregistrement du nouvel expéditeur et de la règle d'inscription)
  - `src/ChefOS.Back/appsettings.json` (`"Accounts": { "RequireConfirmedEmail": true, "AllowRegistration": false }`)
  - `src/ChefOS.Back/appsettings.Development.json` (`RequireConfirmedEmail: false`)
  - `Components/Account/Pages/RegisterConfirmation.razor` (ne plus afficher le lien de confirmation « démo » du modèle Identity)
  - `Components/Account/Pages/Register.razor` (refus côté serveur quand l'inscription est fermée)
  - `Components/Account/Pages/Login.razor` (lien « Créer un compte » masqué quand elle est fermée)
- Test :
  - `tests/ChefOS.Tests/Back/IdentityOutboxEmailSenderTests.cs`
  - `tests/ChefOS.Tests/Back/RegistrationPolicyTests.cs`

**Interfaces :**
- Consumes : `EmailOutbox.Enqueue`, `EmailLayout.Render`, `EmailKind.AccountConfirmation` et `EmailKind.PasswordReset`, `EmailDispatchSignal` (Tasks 3 et 4).
- Produces :
  - `internal sealed class IdentityOutboxEmailSender(IDbContextFactory<ChefOsDbContext> factory, TimeProvider clock, EmailDispatchSignal signal) : IEmailSender<ApplicationUser>`.
  - `public sealed class RegistrationPolicy(UserManager<ApplicationUser> users, IConfiguration configuration, IHostEnvironment environment) { public Task<bool> IsOpenAsync(); }` : ouverte en `Development`, si `Accounts:AllowRegistration` est vrai, ou si aucun utilisateur n'existe.
- **Copie fixée.** Message quand l'inscription est fermée : « Les inscriptions sont fermées. Demandez un accès à l'administrateur de ChefOS. »
- **Sujets :**
  - « Confirmez votre adresse e-mail ChefOS » (inscription et changement d'adresse) ;
  - « Réinitialisez votre mot de passe ChefOS » (lien) ;
  - « Votre code de réinitialisation ChefOS » (code).

- [ ] **Step 1 : écrire les tests.**
  - `ConfirmationLink_IsQueuedInFrench_WithTheEncodedLink` : un lien contenant `&` et `<` est échappé dans le HTML et intact dans le texte ; `Kind = AccountConfirmation` ; `RestaurantId == null`.
  - `PasswordResetLink_IsQueued`.
  - `PasswordResetCode_IsQueued`.
  - `RegistrationPolicyTests`, les quatre cas :
    - `Development` sans réglage : ouverte ;
    - `Production` sans utilisateur : ouverte ;
    - `Production` avec un utilisateur : fermée ;
    - `Production` avec un utilisateur et `AllowRegistration = true` : ouverte.
- [ ] **Step 2 : lancer les tests et vérifier qu'ils échouent.** Attendu : FAIL.
- [ ] **Step 3 : implémenter.**
  - Brancher `options.SignIn.RequireConfirmedAccount = configuration.GetValue("Accounts:RequireConfirmedEmail", true)`.
  - `Register.razor` vérifie `RegistrationPolicy.IsOpenAsync()` à l'affichage et au moment de créer le compte : un POST direct est refusé aussi.
  - Vérifier que `Register.razor` redirige vers `RegisterConfirmation` quand la confirmation est exigée, que `ResendEmailConfirmation` et `ForgotPassword` passent par le nouvel expéditeur, et que le compte de démonstration (`EmailConfirmed = true`) se connecte toujours.
- [ ] **Step 4 : lancer toute la suite.** Attendu : verte.
- [ ] **Step 5 : vérifier à la main avec Mailpit et `Accounts:RequireConfirmedEmail=true`** (passé en ligne de commande).
  - Inscription : e-mail, lien, connexion.
  - Mot de passe oublié : e-mail, lien, nouveau mot de passe, connexion.
- [ ] **Step 6 : commit.** `feat: e-mails des comptes du back et confirmation d'adresse obligatoire`

---

### Task 6 : Pages de compte en français

Spec : § 6 point 10.

**Files :**
- Modify : tous les fichiers de `src/ChefOS.Back/Components/Account/**` qui contiennent encore du texte anglais visible : `Manage/*.razor`, `LoginWith2fa`, `LoginWithRecoveryCode`, `ExternalLogin`, `Lockout`, `ConfirmEmail*`, `ResendEmailConfirmation`, `ForgotPassword*`, `ResetPassword*`, `InvalidUser`, `InvalidPasswordReset`, `AccessDenied`, `Shared/*` et les messages d'état (`StatusMessage`).
- Test : `tests/ChefOS.Tests/Back/AccountPagesFrenchTests.cs`

- [ ] **Step 1 : relever les textes anglais.** Faire l'inventaire des textes anglais visibles dans ces fichiers : titres, libellés, boutons, messages d'état, textes d'aide, attributs `placeholder`, `aria-label` et `title`.
- [ ] **Step 2 : écrire `AccountPagesFrenchTests.NoEnglishLeftInAccountPages`.**
  - Le test parcourt `Components/Account/**/*.razor` et `*.cs`.
  - Il échoue en listant chaque occurrence d'une liste d'expressions anglaises, construite à partir de l'inventaire du Step 1 : au moins 40 expressions, par exemple « Manage your account », « Change password », « Two-factor authentication », « Recovery codes », « Save », « Profile », « Your account has been locked out », « Forgot your password? ».
  - Les noms de routes et les identifiants techniques sont exclus.
- [ ] **Step 3 : lancer le test et vérifier qu'il échoue.** Commande : `dotnet test --filter AccountPagesFrenchTests`. Attendu : FAIL, avec la liste.
- [ ] **Step 4 : traduire en français (fr-BE)**, dans le ton des pages déjà traduites (`Login.razor`, `Register.razor`). La logique des pages ne change pas.
- [ ] **Step 5 : lancer toute la suite** et parcourir `/Account/Manage` et ses sous-pages dans le navigateur.
- [ ] **Step 6 : commit.** `feat: pages de compte entièrement en français`

---

### Task 7 : Corrections des réservations

Spec : § 6 points 1 à 5.

**Files :**
- Modify :
  - `ReservationService.cs` (`ReservationDetail` reçoit `DateTime FreeCancellationUntilUtc` ; synchronisation bornée)
  - `src/ChefOS.Back/Components/Pages/Reservations/ReservationDayView.cs` (`RefundOnCancelByDefault`)
  - `ManualReservationDialog.razor` (choix parmi les créneaux)
  - `VitrineService.cs` (verrous du service dans le contrôle des services réservés ; `CountActiveReservationsOnAsync`)
  - `VitrineSettings.razor` ou `ScheduleEditor.razor` (dialogue de fermeture)
  - `PaymentConfiguration.cs` (index), plus la migration `AddPaymentGatewayIndex`
- Test :
  - `tests/ChefOS.Tests/Back/ReservationDayViewTests.cs`
  - `tests/ChefOS.Tests/Infrastructure/ReservationServiceTests.cs`
  - `tests/ChefOS.Tests/Infrastructure/VitrineServiceTests.cs`

**Interfaces :**
- Produces :
  - `public Task<IReadOnlyList<TimeOnly>> GetManualSlotsAsync(DateOnly date, string serviceName)` sur `ReservationService` : la grille du service ce jour-là, sans filtre de délai ni de capacité.
  - `public Task<int> CountActiveReservationsOnAsync(DateOnly date)` sur `VitrineService` : réservations `Confirmed` et `PendingPayment` du jour local.
- **Valeurs fixées par la spec :** délai global de synchronisation de 4 s, paiements traités du plus récent (Id décroissant) au plus ancien, plafond actuel de 10 conservé.

- [ ] **Step 1 : écrire les tests.**
  - `RefundDefault_UsesTheFrozenDeadline` : `FreeCancellationUntilUtc` est figé à 48 h avant, la règle actuelle du restaurant est à 2 h, et il reste 24 h. La case n'est pas cochée par défaut.
  - `ListForDay_StopsSyncingAfterFourSeconds` : une passerelle factice qui attend 3 s par paiement et 3 paiements ouverts. `ListForDayAsync` rend la main en moins de 5 s.
  - `ListForDay_SyncsNewestPaymentsFirst` : l'ordre d'appel suit les Id décroissants.
  - `RenameCheck_WaitsForTheServiceLock` : une transaction ouverte à part tient `pg_advisory_xact_lock` du service et du jour. `SaveSchedulesAsync` ne rend la main qu'après son commit, et voit la réservation insérée par cette transaction : le renommage est refusé.
  - `ManualSlots_FollowTheServiceGrid` : « Soir » 19:00–22:00, pas de 30 min, donne 19:00, 19:30, …, 21:30.
  - `CountActiveReservationsOn_CountsConfirmedAndPendingOnly`.
- [ ] **Step 2 : lancer les tests et vérifier qu'ils échouent.** Attendu : FAIL.
- [ ] **Step 3 : implémenter.**
  - Le dialogue manuel affiche les créneaux de `GetManualSlotsAsync`. Le refus serveur existant (`ValidateSlotAsync`) reste.
  - Le dialogue de fermeture appelle `CountActiveReservationsOnAsync`. Si le nombre est supérieur à 0, il demande confirmation : « {n} réservation(s) active(s) ce jour-là. Elles ne seront pas annulées automatiquement. Fermer quand même ? »
  - Index sur (`RestaurantId`, `GatewayPaymentId`).
- [ ] **Step 4 : lancer toute la suite**, puis vérifier les deux dialogues dans le back.
- [ ] **Step 5 : commit.** `fix: annulation, synchronisation des paiements, renommage verrouillé, créneaux manuels et fermetures`

---

### Task 8 : Recette sans ligne et rappel de TVA

Spec : § 6 points 6 et 8.

**Files :**
- Modify :
  - `src/ChefOS.Core/Costing/CostIssue.cs` (`CostIssueKind.NoLines`)
  - `src/ChefOS.Core/Costing/RecipeBook.cs`
  - `src/ChefOS.Back/Components/Shared/Ui.cs` (libellé du nouveau problème)
  - `StatsService.cs` (retirer un traitement spécifique devenu inutile, s'il existe)
  - `src/ChefOS.Back/Components/Pages/Receipts/ReceiptReview.razor` (dialogue de rappel)
- Test :
  - `tests/ChefOS.Tests/Core/RecipeBookTests.cs`
  - `tests/ChefOS.Tests/Infrastructure/DashboardServiceTests.cs`
  - `tests/ChefOS.Tests/Back/ReceiptReviewFormTests.cs`

**Interfaces :**
- Produces :
  - `CostIssueKind.NoLines`, avec le nom de la recette et `LineIndex = null` ; libellé « Aucune ligne : coût inconnu ».
  - `ReceiptReviewForm.LinesWithVatToCheck` (int) : nombre de lignes cochées pour application dont la TVA est « à vérifier ».

- [ ] **Step 1 : écrire les tests.**
  - `RecipeWithoutLines_IsIncomplete` : `IsComplete == false` et `Issues` contient `NoLines`.
  - `ParentOfAnEmptySubRecipe_IsIncomplete`.
  - `DashboardAverage_IgnoresRecipesWithoutLines`.
  - `WasteOfARecipeWithoutLines_IsFlaggedIncomplete`.
  - `LinesWithVatToCheck_CountsOnlyAppliedLines`.
- [ ] **Step 2 : lancer les tests et vérifier qu'ils échouent.** Attendu : FAIL.
- [ ] **Step 3 : implémenter.**
  - La règle vit dans `RecipeBook.Compute`.
  - `ReceiptReview` : quand `LinesWithVatToCheck > 0`, la validation ouvre d'abord un `MessageBox` : « {n} ligne(s) ont une TVA à vérifier. Valider quand même ? »
- [ ] **Step 4 : lancer toute la suite.**
- [ ] **Step 5 : commit.** `fix: recette sans ligne au coût inconnu partout, rappel de TVA avant validation`

---

### Task 9 : Jetons de version sur les commandes et les tickets

Spec : § 6 point 7.

**Files :**
- Modify :
  - `src/ChefOS.Core/Sales/Order.cs` et `src/ChefOS.Core/Receipts/Receipt.cs` (`public uint Version { get; set; }`)
  - leurs configurations EF (`.IsRowVersion()`, colonne système `xmin` de Npgsql), plus la migration `AddConcurrencyTokens`
  - `OrderService.cs` et `ReceiptService.cs` (`DbUpdateConcurrencyException` traduite)
  - les DTO et les formulaires d'édition qui renvoient la version lue (`OrderEditor`, `ReceiptReview`)
- Test : `tests/ChefOS.Tests/Infrastructure/ConcurrencyTokenTests.cs`

**Interfaces :**
- Produces :
  - `public sealed class ConcurrentEditException() : DomainValidationException("Modifié entre-temps : rechargez la page")`, dans `ChefOS.Core.Common`, à côté de `DomainValidationException`.
  - Les méthodes d'édition reçoivent la version lue : `uint expectedVersion`. La version figure dans `OrderSummary` et dans le DTO de relecture du ticket.

- [ ] **Step 1 : écrire les tests.**
  - `OrderEditedInTwoTabs_SecondSaveIsRefused` : deux lectures, deux enregistrements ; le second lève `ConcurrentEditException`, et les lignes du premier restent.
  - `ReceiptDraftSavedAfterValidation_IsRefused`.
  - `ReceiptDeletedWhileEdited_IsRefusedCleanly`.
- [ ] **Step 2 : lancer les tests et vérifier qu'ils échouent.** Attendu : FAIL.
- [ ] **Step 3 : implémenter.** Les `ExecuteUpdate` sous condition de statut déjà en place restent tels quels : ils protègent déjà les transitions. Les pages affichent le message dans le snackbar d'erreur habituel.
- [ ] **Step 4 : lancer toute la suite.**
- [ ] **Step 5 : commit.** `fix: jetons de version (xmin) sur les commandes et les tickets`

---

### Task 10 : Erreurs, rôle propriétaire et tests d'intégration du back

Spec : § 6 points 9, 11 et 12.

**Files :**
- Create :
  - `src/ChefOS.Back/Components/Layout/PageErrorBoundary.razor`
  - `tests/ChefOS.Tests/Back/BackSite.cs` (hôte `WebApplicationFactory<Program>` du back)
  - `tests/ChefOS.Tests/Back/BackSiteTests.cs`
- Modify :
  - `src/ChefOS.Back/Components/Layout/MainLayout.razor` (`@Body` enveloppé)
  - `RestaurantService.cs` (`UpdateSettingsAsync` vérifie le rôle)
  - `src/ChefOS.Back/Program.cs` (`public partial class Program;` en fin de fichier, si absent)
  - la page `Settings` (passer l'utilisateur)
  - `tests/ChefOS.Tests/Infrastructure/RestaurantServiceTests.cs`

**Interfaces :**
- Produces :
  - `public Task UpdateSettingsAsync(int restaurantId, string userId, RestaurantSettingsInput input)` : lève `DomainValidationException("Seul le propriétaire peut modifier les paramètres du restaurant")` si l'appartenance n'est pas `Owner`.
  - `BackSite.CreateAsync(PostgresFixture postgres)` :
    - environnement `Development` ;
    - `Email:Sender = Log`, `Payments:Gateway = Demo`, `Demo:SeedOnStartup = false` (sans effet avant la tâche 12) ;
    - deux restaurants, chacun avec un utilisateur propriétaire et un ticket.
  - `BackSite.LoginAsync(HttpClient, string email, string password)` : poste le formulaire de connexion avec le jeton anti-falsification et garde le cookie.

- [ ] **Step 1 : écrire les tests.**
  - `UpdateSettings_ByANonOwner_IsRefused`.
  - `ReceiptFile_OfAnotherRestaurant_Returns404`.
  - `ReceiptFile_WithoutSession_RedirectsToLogin`.
  - `ActiveRestaurant_NotAMember_Returns403`.
  - `ActiveRestaurant_InvalidId_Returns400`.
  - `ActiveRestaurant_Member_Redirects302_AndTheNextPageShowsItsData`.
- [ ] **Step 2 : lancer les tests et vérifier qu'ils échouent.** Attendu : FAIL.
- [ ] **Step 3 : implémenter.**
  - `PageErrorBoundary` : contenu « Une erreur inattendue est survenue. Vos dernières modifications n'ont peut-être pas été enregistrées. », bouton « Recharger » qui appelle `Recover()` puis recharge la page.
  - L'erreur est journalisée (`ILogger`) ; la boundary se réinitialise à chaque navigation.
- [ ] **Step 4 : lancer toute la suite.**
- [ ] **Step 5 : commit.** `feat: boundary d'erreur, rôle propriétaire vérifié par le service, tests d'intégration du back`

---

### Task 11 : Configuration de production vérifiée au démarrage

Spec : § 5.3.

**Files :**
- Create :
  - `src/ChefOS.Infrastructure/Hosting/ProductionConfiguration.cs`
  - `tests/ChefOS.Tests/Infrastructure/ProductionConfigurationTests.cs`
- Modify :
  - `src/ChefOS.Back/Program.cs` et `src/ChefOS.Vitrine/Program.cs` (appel juste après `CreateBuilder`)
  - `DependencyInjection.cs` (l'avertissement Demo garde son texte ; ajouter un avertissement « lecteur de tickets sans clé » au démarrage)
  - `BackSite.cs` et `VitrineSite.cs` (aucun changement attendu : ils restent en `Development`)

**Interfaces :**
- Produces :
  ```csharp
  public static class ProductionConfiguration
  {
      // Liste vide en Development. Chaque message dit quoi régler, en français.
      public static IReadOnlyList<string> Problems(IConfiguration configuration, IHostEnvironment environment);
      // Lève InvalidOperationException avec tous les problèmes, un par ligne, précédés de « Configuration de production incomplète : ».
      public static void EnsureValid(IConfiguration configuration, IHostEnvironment environment);
  }
  ```
- **Règles** (spec, § 5.3) :
  - `ConnectionStrings:ChefOs` non vide ;
  - `Payments:Gateway` présent et égal à `Demo` ou `Mollie` (sans tenir compte de la casse) ;
  - avec `Mollie`, `Mollie:ApiKey` ou `MOLLIE_API_KEY` ;
  - `Vitrine:BaseUrl` absolue et en `https` ;
  - `Email:Sender` présent et égal à `Smtp` ou `Log` ;
  - avec `Smtp`, `Email:Smtp:Host` et `Email:FromAddress`.

- [ ] **Step 1 : écrire les tests unitaires.**
  - `Development_HasNoProblems`.
  - `Production_MissingGateway_IsAProblem`.
  - `Production_MollieWithoutKey_IsAProblem`.
  - `Production_HttpBaseUrl_IsAProblem`.
  - `Production_SmtpWithoutHost_IsAProblem`.
  - `Production_ExplicitDemoAndLog_IsAccepted`.
  - `Production_CompleteMollieSmtp_HasNoProblems`.
- [ ] **Step 2 : écrire les tests d'hôte** `VitrineSite_InProduction_WithoutGateway_FailsToStart` et `BackSite_InProduction_WithoutBaseUrl_FailsToStart`.
  - `WithWebHostBuilder(b => b.UseEnvironment("Production"))`.
  - `CreateClient()` lève une exception dont le message contient « Payments:Gateway », ou « Vitrine:BaseUrl ».
- [ ] **Step 3 : lancer les tests et vérifier qu'ils échouent.** Attendu : FAIL.
- [ ] **Step 4 : implémenter**, puis lancer toute la suite.
- [ ] **Step 5 : commit.** `feat: configuration de production vérifiée au démarrage`

---

### Task 12 : Proxy, clés partagées, santé et migrations au démarrage

Spec : § 5.2 et § 5.4.

**Files :**
- Create :
  - `src/ChefOS.Infrastructure/Hosting/HostingExtensions.cs`
  - `tests/ChefOS.Tests/Infrastructure/HostingTests.cs`
- Modify :
  - `ChefOsDbContext.cs` (implémente `IDataProtectionKeyContext`, `DbSet<DataProtectionKey> DataProtectionKeys`), plus la migration `AddDataProtectionKeys`
  - `src/ChefOS.Back/Program.cs` : migrations selon `Database:MigrateOnStartup`, seed selon `Demo:SeedOnStartup`, bibliothèque de vins
  - `src/ChefOS.Vitrine/Program.cs` : retirer le chargement de la bibliothèque de vins
  - les deux `appsettings.json` et `appsettings.Development.json`
  - `ChefOS.Infrastructure.csproj` : `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`, `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`

**Interfaces :**
- Produces :
  ```csharp
  public static class HostingExtensions
  {
      // En-têtes transférés depuis ReverseProxy:KnownNetworks (CIDR) ; clés de protection en base, nom d'application donné ; contrôles de santé.
      public static IServiceCollection AddChefOsHosting(this IServiceCollection services, IConfiguration configuration, string applicationName);
      // UseForwardedHeaders en premier ; MapHealthChecks("/healthz", liveness) et MapHealthChecks("/healthz/ready", base + migrations).
      public static WebApplication UseChefOsHosting(this WebApplication app);
  }
  ```
- **Noms d'application :** `ChefOS.Back` et `ChefOS.Vitrine`.
- **Options :**
  - `Database:MigrateOnStartup` : `true` par défaut ;
  - `Demo:SeedOnStartup` : `false` par défaut, `true` dans `appsettings.Development.json` du back.
- **Réponse des contrôles de santé.** Texte brut `Healthy`, `Unhealthy` ou `Degraded`, sans détail.

- [ ] **Step 1 : écrire les tests.**
  - `ForwardedProto_FromAKnownNetwork_IsHonoured` : avec `ReverseProxy:KnownNetworks = 10.0.0.0/8` et `X-Forwarded-Proto: https` venant de `10.1.2.3`, `Request.IsHttps` est vrai. Passer par un petit endpoint de test ou par `TestServer` et `RemoteIpAddress`.
  - `ForwardedProto_FromAnUnknownAddress_IsIgnored`.
  - `Ready_ReturnsServiceUnavailable_WhenMigrationsArePending` *(Review Focus 5)* : vitrine sur une base vide non migrée. `/healthz` répond 200, `/healthz/ready` répond 503.
  - `Ready_ReturnsOk_OnAMigratedDatabase`.
  - `DataProtectionKeys_AreStoredInTheDatabase` : après le démarrage du back et une requête qui crée un jeton anti-falsification, `DataProtectionKeys` contient au moins une ligne.
  - `BackStartup_MigratesAnEmptyDatabase_AndLoadsTheWineLibrary` : `MigrateOnStartup = true`, environnement `Production`, configuration complète. `WineReferences.Count() > 0`.
  - `Vitrine_DoesNotSeedTheWineLibrary`.
- [ ] **Step 2 : lancer les tests et vérifier qu'ils échouent.** Attendu : FAIL.
- [ ] **Step 3 : implémenter.**
  - Pipeline : `UseForwardedHeaders` passe avant `UseHsts` et `UseHttpsRedirection`.
  - Le message `fail:` d'EF sur `__EFMigrationsHistory`, noté à l'étape 1, est sans conséquence ; le laisser.
- [ ] **Step 4 : lancer toute la suite.**
- [ ] **Step 5 : commit.** `feat: proxy, clés de protection en base, contrôles de santé, migrations par le back`

---

### Task 13 : Images Docker, composition HTTPS et guide de déploiement

Spec : § 5.1 et § 9 (mise en ligne).

**Files :**
- Create :
  - `src/ChefOS.Back/Dockerfile` et `src/ChefOS.Vitrine/Dockerfile`, construits depuis la racine du dépôt : `docker build -f src/ChefOS.Back/Dockerfile .`
  - `.dockerignore`
  - `deploy/docker-compose.prod.yml`
  - `deploy/Caddyfile`
  - `deploy/.env.example`
  - `docs/deploiement.md`
- Modify :
  - `.gitignore` (`deploy/.env`)
  - `README.md` (lien vers le guide)
  - `docs/superpowers/etape-5-suivi.md` (nouveau : décisions et points reportés)

**Interfaces :**
- **Images.**
  - Build : `mcr.microsoft.com/dotnet/sdk:10.0`. Exécution : `mcr.microsoft.com/dotnet/aspnet:10.0`.
  - Utilisateur `app` (non-root), `ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`.
- **Composition.**
  - Services `db` (postgres:17-alpine, volume `chefos-pg`), `back`, `vitrine` et `caddy` (caddy:2, ports 80 et 443, volumes `caddy-data` et `caddy-config`).
  - Réseau interne `chefos`, avec un sous-réseau fixé dans la composition. `ReverseProxy__KnownNetworks__0` vaut ce sous-réseau.
  - Le back attend `db` (`depends_on` avec `condition: service_healthy`, `pg_isready`). La vitrine attend le back (`service_healthy` sur `/healthz/ready`).
- **Variables de `.env.example`, toutes sans valeur secrète :**
  - `BACK_DOMAIN`, `VITRINE_DOMAIN`, `ACME_EMAIL` ;
  - `POSTGRES_PASSWORD` ;
  - `PAYMENTS_GATEWAY`, `MOLLIE_API_KEY` ;
  - `EMAIL_FROM_ADDRESS`, `EMAIL_FROM_NAME`, `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD` ;
  - `ANTHROPIC_API_KEY` ;
  - `DEMO_SEED_ON_STARTUP`, `DEMO_EMAIL`, `DEMO_PASSWORD` ;
  - `ACCOUNTS_ALLOW_REGISTRATION`.
- **Guide** (`docs/deploiement.md`), sections dans l'ordre de la spec, § 5.1 :
  1. Prérequis.
  2. DNS.
  3. Fichier `.env`.
  4. Premier démarrage.
  5. Premier compte : l'inscription est ouverte tant qu'aucun compte n'existe ; ensuite, `ACCOUNTS_ALLOW_REGISTRATION=true` pour en ajouter.
  6. Sauvegarde et restauration (`docker compose exec db pg_dump …` et `psql`).
  7. Mise à jour.
  8. Vérifications après déploiement (`/healthz/ready`, e-mail de test par « mot de passe oublié », webhook Mollie dans le tableau de bord Mollie).
  9. Limites (une instance par site).

- [ ] **Step 1 : construire les deux images.**
  `docker build -f src/ChefOS.Back/Dockerfile -t chefos-back .` et `docker build -f src/ChefOS.Vitrine/Dockerfile -t chefos-vitrine .`. Attendu : succès.
- [ ] **Step 2 : vérifier la composition.**
  `cp deploy/.env.example /tmp/chefos.env` (valeurs factices), puis `docker compose --env-file /tmp/chefos.env -f deploy/docker-compose.prod.yml config`. Attendu : sortie valide, sans avertissement de variable manquante.
- [ ] **Step 3 : faire un essai de bout en bout en local.**
  - `.env` local : domaines `back.localhost` et `vitrine.localhost` (Caddy utilise alors son autorité interne), `PAYMENTS_GATEWAY=Demo`, `Email` en `Log`, `DEMO_SEED_ON_STARTUP=true`.
  - `docker compose up -d`.
  - `curl -k https://back.localhost/healthz/ready` et `curl -k https://vitrine.localhost/healthz/ready` répondent `Healthy`.
  - La page `https://vitrine.localhost/le-petit-bistro` s'affiche.
  - Enfin `docker compose down -v`.
- [ ] **Step 4 : vérifier le refus de démarrer.** Sans `PAYMENTS_GATEWAY`, `docker compose up back` s'arrête avec le message « Configuration de production incomplète ». Le noter dans le guide (§ Dépannage).
- [ ] **Step 5 : rédiger `docs/superpowers/etape-5-suivi.md`** : décisions prises en cours de route, points reportés.
- [ ] **Step 6 : lancer toute la suite.**
- [ ] **Step 7 : commit.** `feat: images Docker, composition HTTPS avec Caddy et guide de déploiement`
