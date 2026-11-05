# ChefOS

SaaS multi-restaurants qui aide les chefs à connaître le vrai coût de leurs plats et ce qu'ils gagnent ou perdent.

Le produit comprend deux sites qui partagent une même base PostgreSQL :

- **ChefOS.Back** : l'outil du chef. Ingrédients (rendement, historique des prix), fournisseurs, recettes et sous-préparations, coût de revient et food cost recalculés en direct, scan des tickets fournisseurs, ventes, commandes, pertes, inventaires et statistiques, tableau de bord, réglages du restaurant.
- **ChefOS.Vitrine** : le site public de chaque restaurant. Pour l'instant un squelette (« bientôt disponible »), branché sur la base.

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
- **Commandes** (`/orders`) : les commandes prises dans ChefOS (salle, à emporter, téléphone, vitrine), de « Nouvelle » à « Servie » ou « Annulée ». Elles sont comptées automatiquement avec les ventes saisies ; une commande annulée ne compte pas.
- **Pertes** (`/waste`) : un ingrédient, une préparation ou un plat jeté, avec son motif (périmé, casse, erreur, surproduction, autre). Chaque perte est valorisée au prix du jour où elle a eu lieu.
- **Inventaires** (`/inventory`) : le comptage du stock. Un brouillon se corrige ; une fois clôturé, l'inventaire fait foi pour calculer les écarts.
- **Statistiques** (`/stats`) : une période au choix (cette semaine, la semaine dernière, ce mois, le mois dernier, les 4 dernières semaines, ou deux dates). Elle affiche le chiffre d'affaires HT (saisies et commandes), le coût théorique, la marge brute, le food cost théorique, les pertes et le food cost réel ; le chiffre d'affaires par jour ; le tableau des plats (quantités, marge, food cost) ; la matrice de menu engineering (étoiles, vaches à lait, énigmes, poids morts, avec un conseil par case) ; les pertes par raison ; et l'écart d'inventaire. Un chiffre marqué « incomplet » repose sur un prix manquant : c'est un minimum (pour une perte ou un coût) ou un maximum (pour une marge).
- **Tableau de bord** : le chiffre d'affaires HT et la marge brute des 7 derniers jours, aujourd'hui compris, avec un lien vers les statistiques.

### Obtenir le vrai food cost

Le food cost *théorique* se calcule tout seul, depuis les recettes et les ventes. Le food cost *réel* vient de ce qui a vraiment quitté le stock : stock d'ouverture + achats − stock de clôture. Pour l'obtenir :

1. **Faites deux inventaires clôturés qui encadrent la période** : un au début (à la date de début ou avant) et un à la fin (à la date de fin ou après). Seuls les ingrédients comptés aux *deux* inventaires sont comparés ; les autres sont listés sous « Non comptés aux deux inventaires ». Comptez donc les mêmes ingrédients les deux fois.
2. **Passez vos achats par un ticket validé** (page « Tickets »). Seuls les achats d'un ticket appliqué, daté entre les deux inventaires, sont comptés : un achat sans ticket validé sous-estime la consommation réelle de l'ingrédient.

L'écart d'inventaire (consommation réelle − consommation théorique) porte sur l'intervalle entre les deux inventaires, pas sur la période choisie : la page indique les deux dates. Un écart positif signifie qu'il a disparu plus de stock que les ventes et les pertes déclarées ne l'expliquent.

### Données de démonstration

Le jeu de démonstration comprend 8 semaines de ventes saisies au jour (le bistro est fermé le lundi), une quinzaine de commandes sur les 7 derniers jours (dont une annulée), 6 pertes, des tickets appliqués de Metro et des autres fournisseurs, et deux inventaires clôturés : à J-28 et aujourd'hui. Les dates sont relatives au jour du premier lancement. Ce jour-là, la page `/stats` sur les 4 dernières semaines affiche les quatre cases du menu engineering et un écart d'inventaire (beurre, crème et crevettes grises en tête). Dès le lendemain, la période par défaut se termine après le dernier inventaire clôturé (celui du jour du lancement) : il n'y a plus d'écart ni de food cost réel. Choisissez alors une période personnalisée qui se termine le jour du lancement, ou repartez d'une base vierge (`docker compose down -v && docker compose up -d`) avant une démonstration.

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
