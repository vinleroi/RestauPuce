# ChefOS

SaaS multi-restaurants qui aide les chefs à connaître le vrai coût de leurs plats et ce qu'ils gagnent ou perdent.

Le produit comprend deux sites qui partagent une même base PostgreSQL :

- **ChefOS.Back** : l'outil du chef. Ingrédients (rendement, historique des prix), fournisseurs, recettes et sous-préparations, coût de revient et food cost recalculés en direct, scan des tickets fournisseurs, ventes, commandes, pertes, inventaires et statistiques, tableau de bord, réglages du restaurant.
- **ChefOS.Vitrine** : le site public de chaque restaurant. Ambiance, menu, carte des vins et réservation en ligne avec acompte ou prépaiement (voir [Vitrine et réservations](#vitrine-et-réservations)).

Cette première étape pose les fondations et le back du chef. La conception est dans [`docs/superpowers/specs`](docs/superpowers/specs/2026-11-02-fondations-back-chef-design.md), le plan d'implémentation dans [`docs/superpowers/plans`](docs/superpowers/plans/2026-11-02-fondations-back-chef.md).

## Prérequis

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (la base de développement et les tests en dépendent)

## Démarrer le back

```bash
# 1. Base PostgreSQL de développement (127.0.0.1:5433)
docker compose up -d

# 2. L'outil du chef
dotnet run --project src/ChefOS.Back
```

Le back écoute sur <http://localhost:5268>. En environnement `Development`, il applique les migrations au démarrage puis crée, dans une base vide, le restaurant de démonstration « Le Petit Bistro » (fournisseurs, ingrédients avec historique de prix, préparations, plats dont certains sont à surveiller) et son activité, pour que les statistiques soient vivantes dès le premier lancement (voir [Ventes et statistiques](#ventes-et-statistiques)).

### Compte de démonstration

| E-mail | Mot de passe |
|--------|--------------|
| `demo@chefos.local` | `ChefDemo2026!` |

Ces identifiants sont des **valeurs de développement uniquement** : ils viennent de la section `Demo` de `src/ChefOS.Back/appsettings.Development.json` et ne servent qu'au seed de la base locale. Le seed ne s'exécute qu'en `Development` ; ne reprenez jamais ces valeurs ailleurs.

Vous pouvez aussi créer votre propre compte (`/Account/Register`) : un assistant vous fait ensuite créer votre restaurant. Deux comptes ne voient jamais les données l'un de l'autre.

### Repartir d'une base vierge

```bash
docker compose down -v && docker compose up -d
```

Le volume est supprimé : le prochain lancement du back recrée le schéma et les données de démonstration.

## Démarrer la vitrine

```bash
dotnet run --project src/ChefOS.Vitrine
```

La vitrine écoute sur <http://localhost:5108> et partage la base du back (même chaîne de connexion de développement).

## Vitrine et réservations

Chaque restaurant a un site public, à l'adresse `/<slug>` de la vitrine : le chef y publie son ambiance, son menu (avec descriptions et allergènes), sa carte des vins et ses créneaux, et les clients réservent en ligne. Le chef règle tout depuis le back (vitrine, services, fermetures, carte des vins, réservations).

| Application | Adresse |
|-------------|---------|
| Back (le chef) | <http://localhost:5268> |
| Vitrine (le public) | <http://localhost:5108> |
| Le Petit Bistro | <http://localhost:5108/le-petit-bistro> |

### Lancer les deux sites

```bash
docker compose up -d
dotnet run --project src/ChefOS.Back      # en premier : migrations, données de démo, bibliothèque de vins
dotnet run --project src/ChefOS.Vitrine   # dans un second terminal
```

Démarrez le back en premier : il applique les migrations et crée la démo. Dans une base vide, « Le Petit Bistro » est livré avec une vitrine **publiée** (ambiance, couleur, ville de Liège, sans image : un dégradé sert de couverture), des services du mardi au samedi (Midi 12:00-14:00 pour 40 couverts, Soir 19:00-22:00 pour 50, fermé le dimanche et le lundi, un congé annuel dans trois semaines), une phrase publique sur chacun des 12 plats, 15 vins de la bibliothèque avec prix et accords (plus un vin dont la fiche IA est « à relire », non publié), et 9 réservations sur les 7 prochains jours : 6 petites tables confirmées, un groupe de 8 avec convives nommés et plats pré-commandés, une demande en attente de paiement déjà périmée (elle passe en « Expirée » à la première lecture) et une annulation remboursée. Tous les paiements de ces réservations sont des paiements de démonstration.

Règles par défaut : acompte de 10 € par personne jusqu'à 5 couverts ; au-delà, les convives choisissent leurs plats et le prépaiement est de 30 % de la pré-commande ; annulation gratuite jusqu'à 24 h avant ; une demande non payée expire après 30 minutes.

### Paiements

La section `Payments` choisit la passerelle, au démarrage (redémarrez pour en changer) :

- **`Payments:Gateway=Demo`** (par défaut) : paiements simulés, stockés en base. Le bouton « Payer » de la page `/paiement-demo/...` de la vitrine règle le paiement, sans argent réel ni compte ; le back, qui partage la base, peut rembourser. `Vitrine:BaseUrl` est l'adresse publique de la vitrine (<http://localhost:5108> en développement), utilisée pour les pages de paiement, le webhook Mollie et les liens des e-mails.
- **`Payments:Gateway=Mollie`** : paiements réels (ou de test) via [Mollie](https://www.mollie.com/). Rangez la clé de test dans les secrets utilisateur de la vitrine, et du back pour que les annulations remboursent :

```bash
dotnet user-secrets set Mollie:ApiKey test_... --project src/ChefOS.Vitrine
dotnet user-secrets set Mollie:ApiKey test_... --project src/ChefOS.Back
Payments__Gateway=Mollie dotnet run --project src/ChefOS.Vitrine
Payments__Gateway=Mollie dotnet run --project src/ChefOS.Back   # le back aussi, pour rembourser ou vérifier un paiement Mollie
```

  Le back doit tourner avec `Payments:Gateway=Mollie` (et la clé) pour rembourser ou vérifier les paiements Mollie : sinon il refuse avec « Ce paiement a été fait via Mollie : configurez Payments:Gateway=Mollie sur cette application ».

  (La variable d'environnement `MOLLIE_API_KEY` sert de repli.) Sans clé, la passerelle refuse chaque paiement avec un message explicite.

En local, Mollie ne peut pas joindre le webhook (`localhost` n'est pas public) : c'est la page de retour du client qui relit le statut du paiement auprès de la passerelle et confirme la réservation. Il suffit donc de revenir sur le site après avoir payé ; le webhook ne sert que si le client ferme l'onglet avant le retour.

### Fiches de vins par l'IA

`Wines:Describer=Demo` (par défaut) rédige des fiches factices sans réseau. Avec `Wines:Describer=Claude`, la fiche d'un vin saisi à la main est rédigée par Claude avec la clé Anthropic du back (`dotnet user-secrets set Anthropic:ApiKey <clé> --project src/ChefOS.Back`, ou `ANTHROPIC_API_KEY`). Une fiche écrite par l'IA porte le badge « Fiche IA à relire » et n'est publiée que lorsque le chef la valide.

### E-mails

En développement, les e-mails partent vers Mailpit (`docker compose up -d`, boîte de réception sur <http://localhost:8025>) ; avec `Email:Sender=Log`, ils sont seulement écrits dans les journaux. Le lien de suivi (`/<slug>/reservation/<jeton>`) reste la référence du client pour consulter ou annuler sa réservation.

### En production

Le guide pas à pas (serveur, DNS, HTTPS automatique, sauvegarde, mise à jour) est dans **[docs/deploiement.md](docs/deploiement.md)** ; les images Docker et la composition d'exemple sont dans `src/*/Dockerfile` et `deploy/`.

- Hors `Development`, chaque site refuse de démarrer avec « Configuration de production incomplète » si un réglage obligatoire manque (`Payments:Gateway`, `Vitrine:BaseUrl` en https, `Email:Sender`…). La passerelle `Demo`, choisie explicitement, prévient par un avertissement qu'aucun encaissement réel n'a lieu.
- Derrière un proxy, `ReverseProxy:KnownNetworks` (liste CIDR) désigne les réseaux dont on accepte les en-têtes `X-Forwarded-*`.
- L'URL du webhook Mollie est construite sur `Vitrine:BaseUrl`, qui doit être l'adresse publique en HTTPS de la vitrine.
- Pas de données de démo par défaut : le seed ne s'exécute qu'en `Development`, ou avec `Demo:SeedOnStartup=true`.

## Scan des tickets

Le chef téléverse la photo (JPEG, PNG, WebP) ou le PDF d'un ticket ou d'une facture de fournisseur ; l'IA en lit les lignes, le chef corrige le brouillon, puis applique les nouveaux prix aux ingrédients. La lecture se fait avec [Claude](https://www.anthropic.com/claude) (modèle `claude-opus-5-5`, réponse JSON imposée par un schéma). Coût indicatif : **quelques centimes par ticket**.

### Avec Claude

Il faut une clé API Anthropic ([console Anthropic](https://console.anthropic.com/)). Rangez-la dans les secrets utilisateur du back, jamais dans un fichier `appsettings` ni dans git :

```bash
dotnet user-secrets set Anthropic:ApiKey <clé> --project src/ChefOS.Back
```

Autre possibilité : la variable d'environnement `ANTHROPIC_API_KEY` (ou `Anthropic__ApiKey`), qui sert aussi en production. La clé de `Anthropic:ApiKey` l'emporte sur la variable `ANTHROPIC_API_KEY`.

Sans clé, le back démarre quand même : chaque ticket téléversé passe en « échec » avec le message « Clé API Anthropic non configurée », et le chef peut le saisir à la main.

### Démonstration sans clé

`appsettings.Development.json` choisit le lecteur avec `"Receipts": { "Reader": "Claude" }`. Pour une démonstration sans clé ni réseau, passez-le à `Demo` : quel que soit le fichier téléversé, le lecteur renvoie après une courte pause un ticket Metro daté du jour, dont les libellés correspondent aux ingrédients du Petit Bistro (dont un beurre à +10 %) et une ligne sans correspondance (« SACS POUBELLE 100L »).

```bash
Receipts__Reader=Demo dotnet run --project src/ChefOS.Back
```

Le choix du lecteur et la clé sont lus au démarrage : redémarrez le back après les avoir modifiés. Aucun test automatisé n'appelle l'API Anthropic.

## Ventes et statistiques

La section « Activité » du menu regroupe ce qui se passe en salle et en cuisine ; les statistiques les transforment en chiffres.

- **Ventes** (`/sales`) : la grille de la semaine pour les ventes faites hors ChefOS (votre caisse). Une quantité par plat et par jour (ou par service, midi ou soir), ou un total de semaine saisi d'un bloc. Chaque saisie fige le prix de vente et la TVA du plat au moment où elle est enregistrée : changer un prix plus tard ne réécrit pas le chiffre d'affaires passé.
- **Commandes** (`/orders`) : les commandes prises dans ChefOS (salle, à emporter, téléphone, vitrine), de « Nouvelle » à « Servie » ou « Annulée ». Elles sont comptées automatiquement avec les ventes saisies ; une commande annulée ne compte pas. La note d'une commande (allergie…) s'affiche sur sa carte. Une commande encore à traiter d'un jour précédent reste sur le tableau, datée (« hier 23:40 »), jusqu'à ce qu'elle soit servie ou annulée.
- **Pertes** (`/waste`) : un ingrédient, une préparation ou un plat jeté, avec son motif (périmé, casse, erreur, surproduction, autre). Chaque perte est valorisée au prix du jour où elle a eu lieu.
- **Inventaires** (`/inventory`) : le comptage du stock. Un brouillon se corrige, sur plusieurs appareils à la fois : un enregistrement n'envoie que les quantités modifiées ou vidées, et ne touche pas au reste. Une fois clôturé, l'inventaire fait foi pour calculer les écarts. Comptez en fin de journée, après le service et les livraisons du jour.
- **Statistiques** (`/stats`) : une période au choix (cette semaine, la semaine dernière, ce mois, le mois dernier, les 4 dernières semaines, l'intervalle entre les deux derniers inventaires clôturés, ou deux dates). Elle affiche le chiffre d'affaires HT (saisies et commandes), le coût théorique, la marge brute, le food cost théorique, les pertes et le food cost réel ; le chiffre d'affaires par jour ; le tableau des plats (quantités, marge, food cost) ; la matrice de menu engineering (étoiles, vaches à lait, énigmes, poids morts, avec un conseil par case) ; les pertes par raison ; et l'écart d'inventaire. Un chiffre marqué « incomplet » est partiel : un prix manque, une unité ne se convertit pas, la quantité produite d'une préparation est invalide ou une recette n'a aucune ligne. Le food cost réel est marqué « partiel » quand des ingrédients utilisés n'ont pas été comptés aux deux inventaires : il est alors sous-estimé.
- **Tableau de bord** : le chiffre d'affaires HT et la marge brute des 7 derniers jours, aujourd'hui compris, avec un lien vers les statistiques.

### Obtenir le vrai food cost

Le food cost *théorique* se calcule tout seul, depuis les recettes et les ventes. Le food cost *réel* vient de ce qui a vraiment quitté le stock : stock d'ouverture + achats − stock de clôture. Pour l'obtenir :

1. **Faites deux inventaires clôturés qui encadrent la période** : un au début (à la date de début ou avant) et un à la fin (à la date de fin ou après). Seuls les ingrédients comptés aux *deux* inventaires sont comparés ; les autres sont listés sous « Non comptés aux deux inventaires ». Comptez donc les mêmes ingrédients les deux fois. **Comptez en fin de journée, après le service et les livraisons du jour** : les ventes et les achats du jour de l'inventaire de clôture comptent dans l'écart, ceux du jour de l'inventaire d'ouverture n'y comptent pas.
2. **Passez vos achats par un ticket validé** (page « Tickets »). Seuls les achats d'un ticket appliqué, daté entre les deux inventaires, sont comptés : un achat sans ticket validé sous-estime la consommation réelle de l'ingrédient.

L'écart d'inventaire (consommation réelle − consommation théorique) porte sur l'intervalle entre les deux inventaires, pas sur la période choisie : la page indique les deux dates. Un écart positif signifie qu'il a disparu plus de stock que les ventes et les pertes déclarées ne l'expliquent.

### Données de démonstration

Le jeu de démonstration comprend 8 semaines de ventes saisies au jour (le bistro est fermé le lundi), une quinzaine de commandes sur les 7 derniers jours (dont une annulée), 6 pertes, des tickets appliqués de Metro et des autres fournisseurs, et deux inventaires clôturés : à J-28 et aujourd'hui. Les dates sont relatives au jour du premier lancement. Ce jour-là, la page `/stats` sur les 4 dernières semaines affiche les quatre cases du menu engineering et un écart d'inventaire (beurre, crème et crevettes grises en tête). Dès le lendemain, la période par défaut se termine après le dernier inventaire clôturé (celui du jour du lancement) : il n'y a plus d'écart ni de food cost réel. Choisissez alors « Entre les deux derniers inventaires » (ou une période personnalisée qui se termine le jour du lancement), ou repartez d'une base vierge (`docker compose down -v && docker compose up -d`) avant une démonstration.

## Tests

```bash
dotnet test
```

**Docker doit tourner** : les tests d'infrastructure démarrent un conteneur PostgreSQL jetable (Testcontainers) et ne touchent pas à la base de développement.

## Migrations

Le schéma est géré par EF Core dans `ChefOS.Infrastructure`. Pour créer une migration après une modification du modèle :

```bash
dotnet tool restore
dotnet ef migrations add NomDeLaMigration -p src/ChefOS.Infrastructure -o Data/Migrations
```

En développement, le back applique les migrations en attente à chaque démarrage. Pour les appliquer à la main : `dotnet ef database update -p src/ChefOS.Infrastructure`. L'outil `dotnet ef` utilise la base locale du `docker-compose.yml`.

## Structure

```
src/
  ChefOS.Core/            Règles métier pures : unités, coût de revient, food cost, TVA (sans dépendance)
  ChefOS.Infrastructure/  EF Core + PostgreSQL, Identity, services, isolation par restaurant, données de démo
  ChefOS.Back/            L'outil du chef (Blazor Server + MudBlazor)
  ChefOS.Vitrine/         Le site public de chaque restaurant
tests/ChefOS.Tests/       Tests unitaires et d'infrastructure (PostgreSQL via Testcontainers)
```

## Conventions

- Interface en français, culture `fr-BE` : montants « 12,60 € », saisie décimale avec virgule.
- Les taux (TVA, seuil d'alerte) sont stockés en fraction (`0.12`) et affichés en pourcentage (« 12 % »).
- Chaque donnée métier appartient à un restaurant ; l'isolation est garantie par un filtre global du contexte EF et par un contrôle à l'écriture.
- Hors `Development`, la chaîne de connexion `ConnectionStrings:ChefOs` doit être fournie (par exemple via la variable d'environnement `ConnectionStrings__ChefOs`) ; ni migrations automatiques ni données de démo.
