# ChefOS — Étape 5 : prêt pour la production

**Date** : 2026-11-12
**Statut** : l'utilisateur a demandé de passer directement au code (« go for it »). **Tous les choix de ce document sont donc ceux de Claude.** Ils partent du périmètre proposé à la fin de l'étape 4 et des points de suivi `docs/superpowers/etape-*-suivi.md`.
**Base** : branche `feature/etape-5-production`, créée depuis `main` (f0f8ff4), qui contient les étapes 1 à 4.

## 1. Intention

Les étapes 1 à 4 forment un produit complet qui tourne sur le Mac du chef. L'étape 5 le rend capable de tourner sur internet pour de vrais restaurants, sans mauvaise surprise :

- les clients de la vitrine reçoivent de **vrais e-mails** (confirmation avec lien d'annulation, annulation, remboursement), et les comptes du back peuvent confirmer leur adresse et réinitialiser leur mot de passe ;
- **toutes les journées et les heures du back sont celles du restaurant**, pas celles du serveur ;
- les deux sites **se déploient derrière HTTPS** avec une configuration explicite, vérifiée au démarrage ;
- les **points de suivi** qui toucheraient des clients réels sont soldés.

**Pourquoi maintenant.**
- Une fois hébergé, le serveur tourne en UTC. Une commande passée à 00:30 à Bruxelles tomberait alors la veille dans les stats, « aujourd'hui » changerait à 01:00 (02:00 en été), et toutes les heures affichées avec `ToLocalTime()` seraient décalées d'une ou deux heures.
- Sans e-mail, le client n'a ni confirmation ni lien pour annuler, et un chef qui a oublié son mot de passe est bloqué.
- Sans configuration de production, un site hébergé démarre en silence avec des paiements simulés, et chaque redéploiement déconnecte tout le monde.

## 2. Objectifs, non-objectifs, anti-objectifs

**Objectifs**
1. E-mails réels et fiables (file d'attente, nouvelles tentatives), en français, pour les clients de la vitrine et pour les comptes du back (§ 3).
2. Le fuseau du restaurant partout dans le back : « aujourd'hui », les journées des ventes, stats, pertes, inventaires et commandes, et l'affichage des heures (§ 4).
3. Les deux sites prêts à héberger : images Docker, proxy HTTPS, clés partagées en base, migrations au démarrage, contrôles de santé, configuration vérifiée et guide de déploiement (§ 5).
4. Les corrections reportées du § 6.

**Non-objectifs (pas dans cette étape)**
- Choisir l'hébergeur, créer des comptes (hébergeur, Mollie, fournisseur d'e-mails), acheter un domaine, déployer réellement : c'est à l'utilisateur, et le guide explique comment faire.
- Prévenir le restaurateur d'une nouvelle réservation ; SMS ; rappels la veille.
- Écran de suivi des e-mails dans le back : les journaux suffisent pour l'instant.
- Invitations d'équipe, suppression d'un restaurant, plusieurs instances d'un même site.
- **Un compte de paiement par restaurant (Mollie Connect).** Une instance utilise une seule clé Mollie : tous ses acomptes arrivent sur le même compte. Pour un pilote avec le restaurant de l'utilisateur, c'est voulu. Ouvrir la plateforme à d'autres restaurants demandera Mollie Connect, dans une étape ultérieure.
- Grands découpages de fichiers (`ReservationService`, `StatsPage`, `ReceiptReview`…), sauf là où une tâche y touche déjà.
- Le test avec un vrai ticket et une vraie clé : l'utilisateur l'a reporté, il se fera à part.
- Autres langues que le français.

**Anti-objectifs (ce qui serait un échec même si « ça marche »)**
- Un e-mail client perdu sans trace parce que le serveur SMTP était indisponible un instant.
- En fonctionnement normal, un même e-mail envoyé deux fois pour un même événement.
- Un e-mail envoyé pour un événement qui n'a finalement pas eu lieu (transaction annulée).
- Un secret (mot de passe SMTP, clés API) dans un fichier versionné ou dans les journaux. Les adresses restent masquées dans les journaux, comme aujourd'hui.
- Un site hébergé qui démarre en silence dans une configuration de démo ou incomplète.
- Un changement de règle métier visible (prix, capacité, annulation, coûts). Seules les journées et les heures se recalent sur le restaurant.
- Une régression : les tests existants restent verts.

## 3. E-mails réels

### 3.1 Envoi

- **Protocole.** SMTP standard via **MailKit**. Il fonctionne avec Brevo, Postmark, Mailjet, OVH, Gmail…, sans dépendre d'une API propriétaire.
- **Configuration**, section `Email` :

  | Clé | Rôle | Défaut |
  |---|---|---|
  | `Sender` | `Smtp` ou `Log` | `Log` |
  | `FromAddress` | adresse d'expédition | — |
  | `FromName` | nom d'expéditeur | « ChefOS » |
  | `Smtp:Host` | serveur SMTP | — |
  | `Smtp:Port` | port | 587 |
  | `Smtp:Security` | `StartTls`, `SslOnConnect`, ou `None` (développement local seulement) | `StartTls` |
  | `Smtp:Username` | identifiant | — |
  | `Smtp:Password` | mot de passe | — |

  Le mot de passe est un **secret** : user-secrets en développement, variable d'environnement `Email__Smtp__Password` en production, jamais dans un appsettings.
- **Développement.**
  - **Mailpit** s'ajoute au `docker-compose.yml` : SMTP sur 127.0.0.1:1025, interface web sur 127.0.0.1:8025.
  - `appsettings.Development.json` (back et vitrine) choisit `Sender = Smtp` vers Mailpit. On voit ainsi les vrais e-mails rendus, sans rien envoyer dehors.
  - `Log` reste disponible, et c'est l'expéditeur des tests.
- **Adresses.**
  - Le `From` est toujours l'adresse de la plateforme (SPF et DKIM du domaine d'envoi).
  - Pour les e-mails aux clients d'un restaurant, le `Reply-To` est l'adresse publique du restaurant (`VitrineProfile.PublicEmail`) quand elle existe. Le client qui répond écrit donc au restaurant.
- **Format.**
  - Texte brut et HTML simple : une colonne, le nom du restaurant en titre, sa couleur d'accent, un bouton pour le lien.
  - Toutes les valeurs saisies sont échappées dans le HTML.

### 3.2 File d'attente (outbox)

- **Table.** Table globale `OutgoingEmails`, sans filtre par restaurant ; elle n'est exposée par aucune page. Elle contient :
  - destinataire, reply-to, sujet, texte, HTML ;
  - type d'e-mail et `RestaurantId` (nullable), pour le suivi ;
  - création, prochain essai, nombre d'essais, date d'envoi ;
  - dernière erreur, sans donnée personnelle ;
  - statut : en attente, envoyé ou abandonné.
- **Écriture.**
  - Les services n'appellent plus SMTP. Ils ajoutent le message **dans la même transaction** que le changement qui le provoque (réservation confirmée, annulée, remboursement demandé).
  - Un e-mail n'existe donc que si l'événement a eu lieu. Il n'est jamais envoyé pour une transaction annulée.
  - Là où le changement est déjà fait en plusieurs étapes, par exemple après un appel à la passerelle de paiement, le message est écrit avec l'étape qui confirme l'événement.
- **Envoi.**
  - Un service d'arrière-plan tourne **dans les deux sites**.
  - Il réserve de façon atomique les messages dus, de sorte que deux processus ne prennent jamais le même. Il les envoie, puis les marque.
  - Il se réveille toutes les ~5 s, et tout de suite après un ajout fait dans le même processus.
  - Un arrêt du site pendant un envoi ne bloque pas le message pour toujours : la réservation expire et le message repart.
- **Nouvelles tentatives.**
  - **Serveur SMTP injoignable** (connexion impossible, authentification refusée, délai dépassé) : c'est une panne du serveur, pas un échec du message.
    - Le passage s'arrête, sans compter d'essai.
    - Le serveur est retenté toutes les 30 s.
    - Les messages en attente partent dès qu'il répond de nouveau.
  - **Erreur temporaire liée au message** (réponse SMTP 4xx) : nouvel essai après 1 min, 5 min, 30 min, 2 h, puis 6 h. Ensuite le message est abandonné et une erreur est journalisée.
  - **Erreur permanente** (réponse SMTP 5xx, adresse refusée) : abandon immédiat, journalisé.
  - **Message trop vieux** : un message qui n'a pas pu partir dans les 24 h qui suivent sa création est abandonné, et journalisé. Une confirmation de réservation ou un lien de mot de passe n'a plus de sens au-delà.
- **Données personnelles.**
  - Le texte et le HTML sont effacés dès l'envoi ou l'abandon.
  - Les lignes sont supprimées après 30 jours.
- **Journaux.** Destinataire masqué, sujet, type et identifiant du message. Jamais le corps, au niveau Information ou plus haut.

### 3.3 Quels e-mails

**Clients de la vitrine.**
- Dates et heures dans le fuseau du restaurant.
- Lien personnel : `{Vitrine:BaseUrl}/{slug}/reservation/{jeton}`. Le jeton est déjà stocké sur la réservation.
- **Le lien fonctionne toujours**, même si la vitrine n'est pas publiée (réservation manuelle d'un restaurant sans vitrine) ou si son slug a changé.
  - Pour cette page seulement, et l'annulation qu'elle permet, le restaurant est retrouvé par le jeton.
  - Un slug périmé redirige vers le slug courant.
  - Les autres pages de la vitrine gardent la règle de l'étape 4 : 404 si la vitrine n'est pas publiée.
  - Le jeton (256 bits) est un secret : seul son détenteur voit la page.
- On peut répondre au restaurant (`Reply-To`, § 3.1).

| # | Déclencheur | Contenu |
|---|---|---|
| 1 | Réservation confirmée : sans paiement dû, paiement reçu, ou réservation manuelle du back avec une adresse e-mail saisie | Date, heure, service, couverts ; montant réglé s'il y en a un ; date limite d'annulation gratuite (celle figée sur la réservation) ; lien pour voir ou annuler ; adresse et téléphone du restaurant s'ils sont publiés |
| 2 | Annulation par le client | Accusé de réception ; remboursement prévu (montant) ou non, selon la règle figée |
| 3 | Annulation par le restaurant | Annonce de l'annulation ; remboursement s'il a été demandé (montant) ; invitation à contacter le restaurant |
| 4 | Paiement reçu après expiration, la place ayant été reprise, et donc remboursé | Le paiement est arrivé trop tard ; il est remboursé ; lien pour réserver à nouveau |

Pas d'e-mail pour l'arrivée, le no-show, ni pour une réservation en attente de paiement qui expire sans paiement.

**Comptes du back (Identity).**
- Confirmation d'adresse à l'inscription, changement d'adresse, mot de passe oublié. Ces e-mails sont en français et passent par la même file.
- La confirmation d'adresse devient **obligatoire pour se connecter** (`SignIn.RequireConfirmedAccount`).
  - Option `Accounts:RequireConfirmedEmail` : vrai par défaut, faux dans `appsettings.Development.json`.
  - Raison : Identity n'envoie le lien de mot de passe oublié qu'à une adresse confirmée. Sans confirmation, un chef qui a oublié son mot de passe serait bloqué.
  - Le compte de démonstration est déjà confirmé.
- **Inscription contrôlée.**
  - Hors développement, `/Account/Register` n'est ouvert que s'il n'existe encore aucun compte (création du premier compte), ou si `Accounts:AllowRegistration` vaut vrai. En développement, l'inscription reste ouverte.
  - Sinon, la page indique que les inscriptions sont fermées, et le lien « Créer un compte » disparaît de la connexion.
  - Raison : sur une instance hébergée, une inscription libre permettrait à n'importe qui de publier une vitrine et d'envoyer des e-mails à des inconnus depuis l'adresse de la plateforme.

## 4. Fuseau horaire du restaurant dans le back

### 4.1 Principe

Il y a deux sortes de dates en base. Leur stockage ne change pas.

- **Les jours.** Ce sont des étiquettes de calendrier, stockées à minuit UTC : `SalesEntry.Date`, `WasteEntry.Date`, `StockCount.Date`, `Receipt.ReceiptDate`, et les entrées de prix venues d'un ticket (`IngredientPrice` dont la source est le ticket). **Ils ne sont jamais convertis** : le 12/11 reste le 12/11.
- **Les instants.** Ce sont de vrais moments UTC : `Order.CreatedAt` et `StatusChangedAt`, `Receipt.CreatedAt` et `AppliedAt`, `WasteEntry.CreatedAt`, `StockCount.ClosedAt`, `SalesEntry.UpdatedAt`, les entrées de prix manuelles, `Restaurant.CreatedAt`. Pour les grouper par jour ou les afficher, **on les convertit dans le fuseau du restaurant** (`Restaurant.TimeZoneId`, `Europe/Brussels` par défaut), avec `RestaurantClock`.

« Aujourd'hui », c'est la date locale du restaurant à l'instant donné par le `TimeProvider`.

Il n'y a pas de migration de données : aucune donnée de production n'existe, et la base de développement se ré-amorce.

### 4.2 Changements

- **Un service unique.** Il est scoped et donne, pour le restaurant courant :
  - aujourd'hui ;
  - le jour local d'un instant ;
  - les bornes UTC d'un jour local ;
  - un instant converti pour l'affichage.

  Il remplace `UtcDays` et tous les `DateTime.UtcNow`, `DateTime.Now`, `DateTime.Today` et `ToLocalTime()` des services et des pages du back.
- **Horloge.** Tous les services prennent l'heure du `TimeProvider` ; aujourd'hui, seuls certains le font.
- **Règle « pas dans le futur »** (tickets, pertes, inventaires). Un jour est refusé s'il tombe après demain dans le fuseau du restaurant, et non plus en UTC. Demain reste permis, comme aujourd'hui : un bon de livraison peut être daté du lendemain.
- **Commandes du jour.** Ce sont celles créées entre minuit et minuit, heure locale. La règle « les commandes encore ouvertes de la veille restent visibles » ne change pas.
- **Stats et tableau de bord.**
  - Une ligne de commande compte pour le jour local de sa création.
  - Les périodes prédéfinies et les 7 derniers jours du tableau de bord partent d'aujourd'hui, heure locale.
  - Les variations de prix récentes comptent leurs jours à partir de maintenant.
- **Ventes, pertes, inventaires.** La semaine courante et les dates par défaut partent d'aujourd'hui, heure locale. La date maximale est demain, heure locale.
- **Affichage.** Les heures et les dates d'instants sont toujours dans le fuseau du restaurant, jamais dans celui du serveur :
  - tableau des commandes et « actualisé à » ;
  - tickets créés et appliqués ;
  - historique et graphique des prix.

  Les entrées de prix venues d'un ticket s'affichent à leur jour, sans conversion.
- **Démo (seed).**
  - `TimeProvider` à la place de `DateTime.UtcNow`.
  - Les 30 jours d'activité de démonstration sont calculés depuis aujourd'hui à Bruxelles.
  - Les tests du seed utilisent une horloge fixe, et ne dépendent donc plus de la date du jour.

### 4.3 Tests attendus

- Un restaurant à Bruxelles : une commande à 00:30 heure locale (23:30 UTC la veille) compte pour le bon jour dans la liste des commandes, les stats et le tableau de bord. Même vérification autour du passage heure d'été/heure d'hiver.
- Un restaurant à Auckland, dans la même base, garde ses propres journées.
- Les tests tournent avec le fuseau du processus forcé à UTC, ou indépendamment de lui, pour reproduire un serveur hébergé.

## 5. Mise en ligne

### 5.1 Images et composition

- **Images.** Un `Dockerfile` par site : build .NET 10 en plusieurs étapes, image d'exécution non-root, port 8080.
- **Composition d'exemple** `deploy/docker-compose.prod.yml` :
  - PostgreSQL 17 (volume persistant), le back, la vitrine ;
  - **Caddy** en frontal, avec HTTPS automatique (Let's Encrypt), WebSockets compris (indispensable aux pages interactives Blazor) ;
  - deux noms de domaine, réglables : un pour le back (ex. `app.mondomaine.be`) et un pour la vitrine (ex. `resto.mondomaine.be`).
- **Variables.** Elles sont dans un fichier `.env` non versionné. Un `deploy/.env.example` versionné, sans aucun secret, liste chaque variable.
- **Guide** `docs/deploiement.md`, en français, pour quelqu'un qui n'a jamais hébergé de site :
  - prérequis : un serveur avec Docker (VPS), un nom de domaine, un compte SMTP, un compte Mollie ;
  - DNS ;
  - remplissage du `.env` ;
  - premier démarrage et création du premier compte ;
  - sauvegarde et restauration de la base (`pg_dump`) ;
  - mise à jour ;
  - vérifications après déploiement : santé, e-mail de test, webhook Mollie ;
  - une instance par site.

### 5.2 Derrière un proxy

- **En-têtes transférés.** `X-Forwarded-For`, `-Proto` et `-Host` ne sont acceptés que des réseaux configurés (`ReverseProxy:KnownNetworks`, liste CIDR). Dans la composition d'exemple, c'est le réseau Docker interne.
- **HTTPS.** La redirection HTTPS et HSTS tiennent compte du schéma transmis par le proxy : pas de boucle de redirection, et des cookies `Secure`.
- **Adresses publiques.** Le webhook Mollie et les liens des e-mails sont construits sur `Vitrine:BaseUrl`, l'adresse publique HTTPS de la vitrine.

### 5.3 Configuration vérifiée au démarrage

**Une seule adresse publique de la vitrine** : `Vitrine:BaseUrl`, lue par les deux sites. Elle remplace `Payments:BaseUrl`.

Hors `Development`, le site **refuse de démarrer**, avec un message en français qui dit quoi régler, dans les cas suivants.

| Réglage | Refusé si… |
|---|---|
| `ConnectionStrings:ChefOs` | vide (déjà le cas aujourd'hui) |
| `Payments:Gateway` | absent ou autre que `Demo` ou `Mollie` |
| `Payments:Gateway = Mollie` | pas de clé Mollie |
| `Vitrine:BaseUrl` | absente, pas absolue, ou pas en `https://` |
| `Email:Sender` | absent |
| `Email:Sender = Smtp` | sans hôte ou sans adresse d'expédition |

`Demo` choisi explicitement reste permis : c'est le cas d'une démo hébergée pour des investisseurs. Le site journalise alors un avertissement, et la page de paiement de démonstration le dit déjà. Le lecteur de tickets sans clé garde son comportement actuel (le site démarre, et chaque lecture échoue avec un message clair), avec un avertissement au démarrage.

### 5.4 Base de données et démarrage

- **Migrations.**
  - Le **back** applique les migrations au démarrage, dans tous les environnements : option `Database:MigrateOnStartup`, vraie par défaut. Le verrou de migration d'EF Core empêche deux instances de migrer en même temps.
  - Il charge ensuite la bibliothèque de vins.
  - La **vitrine** ne migre pas et ne charge plus la bibliothèque. La course au premier démarrage sur une base vide disparaît.
- **Démo hébergée.**
  - Option `Demo:SeedOnStartup` : vraie dans `appsettings.Development.json`, fausse par défaut ailleurs. Elle sert à remplir « Le Petit Bistro » sur une démo hébergée.
  - Hors développement, le mot de passe du compte de démonstration vient d'une variable d'environnement.
- **Clés de protection des données.**
  - Elles servent aux cookies de connexion, aux jetons anti-falsification et aux circuits Blazor.
  - Elles sont stockées en base, avec un nom d'application distinct par site.
  - Un redéploiement ne déconnecte plus personne et n'invalide plus les formulaires ouverts.
- **Contrôles de santé**, sur les deux sites, sans aucune donnée sensible :
  - `/healthz` : le processus répond ;
  - `/healthz/ready` : la base est joignable et aucune migration n'est en attente. La vitrine n'est « prête » qu'une fois le schéma installé par le back.

## 6. Corrections reportées

**Réservations et paiements**
1. **Annulation depuis le back.** La case « rembourser » est cochée par défaut selon la date limite figée sur la réservation (`FreeCancellationUntilUtc`), et non selon la règle actuelle du restaurant.
2. **Vérification automatique des paiements en attente** (back).
   - Elle est bornée à 4 s au total, et traite d'abord les paiements les plus récents.
   - Index sur (`RestaurantId`, `GatewayPaymentId`).
3. **Renommer ou supprimer un service.** Le contrôle « réservations à venir » prend les verrous du service, les mêmes qu'une réservation. Il ne peut plus croiser une réservation faite au même moment.
4. **Réservation manuelle.**
   - L'heure est choisie parmi les créneaux du service, comme sur la vitrine.
   - Le serveur refuse une heure hors de la grille.
5. **Fermeture exceptionnelle.**
   - S'il existe déjà des réservations actives ce jour-là, le back en indique le nombre.
   - Il rappelle qu'elles ne sont pas annulées automatiquement, et demande confirmation.

**Coûts et tickets**

6. **Recette sans ligne.**
   - Une recette sans ligne a un coût **inconnu** partout : pertes, liste des recettes, moyenne du tableau de bord, stats.
   - La règle vit à un seul endroit (`RecipeBook` / `CostResult.IsComplete`).
7. **Jetons de version** (`xmin`) sur `Order` et `Receipt`. Une modification croisée (deux onglets, deux personnes) affiche « Modifié entre-temps : rechargez la page » au lieu d'écraser l'autre.
8. **Ticket.** Avant de valider, un rappel s'affiche s'il reste des lignes « TVA à vérifier ».

**Back en général**

9. **`ErrorBoundary` autour du contenu de la page.**
   - Une erreur inattendue affiche un message en français avec un bouton « Recharger », au lieu de la barre d'erreur générique.
   - L'erreur est journalisée.
10. **Pages de compte encore en anglais** : `Account/Manage/**`, double authentification, connexions externes, et tous les textes des e-mails Identity. Elles passent en français (fr-BE).
11. **Rôle propriétaire.** `RestaurantService.UpdateSettingsAsync` vérifie lui-même le rôle Owner : la page ne doit pas être la seule barrière.
12. **Tests d'intégration** (`WebApplicationFactory` sur le back), en complément de ceux qui existent :
    - la chaîne claim → restaurant courant → filtre ;
    - les réponses de `/account/active-restaurant` (403, 400, 302) ;
    - `/receipts/{id}/file` : 404 pour le ticket d'un autre restaurant, redirection vers la connexion sans session.

## 7. Contraintes

- **Même pile** : .NET 10, Blazor Web App, MudBlazor 9, EF Core 10 + Npgsql, PostgreSQL 17, xUnit + Testcontainers.
- **Nouvelles dépendances**, limitées à :
  - MailKit ;
  - la persistance EF Core des clés de protection des données ;
  - les contrôles de santé EF Core ;
  - un conteneur Mailpit (Testcontainers générique) pour les tests d'envoi.
- **Secrets** : user-secrets en développement, variables d'environnement en production. Jamais dans un fichier versionné ni dans les journaux. Claude ne manipule jamais les clés de l'utilisateur.
- **Aucun test n'appelle un service externe réel** (SMTP réel, Mollie, Anthropic). Mailpit en conteneur est local.
- **Multi-restaurant.** Tout ce qui appartient à un restaurant reste filtré. La file d'e-mails et les clés de protection sont globales, mais aucune page ne les expose.
- **Langue.** L'interface, les messages et les e-mails sont en français (fr-BE).
- **Migration EF.** Une seule (ou une par tâche, au choix) : file d'e-mails, clés de protection, index des paiements, jetons de version.
- **Processus.**
  - TDD, et une revue par tâche.
  - Commits sur `feature/etape-5-production`, terminés par la ligne `Co-Authored-By` du projet.
  - Le dossier `.superpowers/` n'est jamais commité.

## 8. Laissé au constructeur

- Les noms des types, la découpe en fichiers, la forme exacte des gabarits HTML.
- La mécanique de réservation atomique des messages (`FOR UPDATE SKIP LOCKED`, bail avec date d'expiration, etc.).
- Le découpage en tâches et leur ordre.
- Les textes exacts des e-mails, dans l'esprit du § 3.3.
- La façon de forcer UTC dans les tests (§ 4.3).
- Les tests à ajouter en plus de ceux cités.

## 9. Ce qui prouve que c'est fini

- **Avec Mailpit.**
  - Réserver sur la vitrine : l'e-mail de confirmation arrive, et son lien ouvre la réservation et permet d'annuler. L'e-mail d'annulation arrive ensuite.
  - Arrêter Mailpit, réserver, relancer Mailpit : l'e-mail part dans les 30 s qui suivent la reprise.
  - Une réservation manuelle sur une vitrine non publiée envoie un lien qui ouvre bien la réservation.
- **Comptes.**
  - Inscription, e-mail de confirmation, connexion. « Mot de passe oublié » fonctionne de bout en bout.
  - En production, une deuxième inscription est refusée tant que `Accounts:AllowRegistration` n'est pas activé.
- **Fuseau horaire.** Processus en UTC : une commande à 00:30 heure de Bruxelles compte pour le bon jour dans le tableau des commandes, le tableau de bord et les stats.
- **Mise en ligne.**
  - Les deux images se construisent.
  - `docker compose -f deploy/docker-compose.prod.yml config` est valide.
  - En mode Production avec une configuration complète, les deux sites démarrent et `/healthz/ready` répond OK.
  - Avec une configuration incomplète, ils refusent de démarrer avec le bon message.
- **Qualité.** Build sans avertissement, et tous les tests verts.
