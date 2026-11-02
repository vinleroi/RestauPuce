# ChefOS — Étape 1 : Fondations + Back chef

**Date** : 2026-11-02
**Statut** : design validé, en attente de relecture
**Base de départ** : `~/Desktop/axWebosV01/restaurantErp-devAHAA/AxWebOS` (référence uniquement, non modifiée)

---

## 1. Vision produit

ChefOS est un **SaaS multi-restaurants** qui aide les chefs à connaître le vrai coût de leurs plats et ce qu'ils gagnent ou perdent. Le produit comprend deux sites qui partagent une même base de données :

- **ChefOS.Back** : l'outil du chef (recettes, coûts, scan des tickets fournisseurs, ventes, statistiques, commandes).
- **ChefOS.Vitrine** : le site public de chaque restaurant (menu, réservation de table, prépaiement).

### Découpage en 4 étapes

Chaque étape suit son propre cycle spec → plan → code. **Ce document détaille uniquement l'étape 1.**

| # | Étape | Contenu |
|---|-------|---------|
| 1 | **Fondations + back chef** | Solution, comptes, multi-restaurant, ingrédients avec rendement, recettes et sous-préparations, coût de revient, food cost |
| 2 | Scan de tickets | Photo du ticket → IA vision (API Claude) → écran de validation → correspondances retenues (`SupplierAlias`) → mise à jour des prix (`IngredientPrice`) |
| 3 | Ventes et stats | Ventes saisies à la main (jour, service ou semaine) et ventes issues des commandes, fusionnées ; marges, food cost réel, gaspillage (`WasteEntry`), inventaires (`StockCount`), écart théorique/réel ; réception des commandes |
| 4 | Vitrine | Menu public `/{slug}`, réservation, prépaiement (Mollie ou Stripe, à trancher dans la spec de l'étape 4) |

### Décisions déjà prises (toutes étapes)

- Nouvelle solution propre ; l'ancien code ne sert que de référence.
- SaaS multi-restaurants dès le départ.
- Lecture des tickets par IA vision (API Claude) avec validation humaine obligatoire avant toute mise à jour des prix.
- Les ventes viennent de deux sources, fusionnées : la saisie manuelle et les commandes passées dans l'outil.
- Le coût réel tient compte du rendement des ingrédients ; le gaspillage et l'écart d'inventaire sont suivis (étape 3).

---

## 2. Stack technique

| Élément | Choix | Raison |
|---------|-------|--------|
| Framework | **.NET 10 (LTS)** | SDK installé (10.0.107), support long |
| Back | Blazor Web App, rendu **Interactive Server** | UI riche, recalcul en direct |
| Vitrine | Blazor Web App, rendu **SSR statique** | léger, bon référencement |
| UI back | **MudBlazor** | composants tableau, formulaire et graphique prêts à l'emploi |
| Base | **PostgreSQL** partout (Docker en dev) | un seul jeu de migrations, aucun écart dev/prod |
| ORM | EF Core 10 + Npgsql | |
| Auth | ASP.NET Core Identity (email + mot de passe) | |
| Tests | xUnit + Testcontainers (PostgreSQL) | |

---

## 3. Structure de la solution

Emplacement : `~/Desktop/ChefOS/`, dans un dépôt git à part.

```
ChefOS/
├── ChefOS.sln
├── docker-compose.yml              → PostgreSQL de dev
├── src/
│   ├── ChefOS.Core/                → entités + logique pure, aucune dépendance
│   ├── ChefOS.Infrastructure/      → DbContext, filtre multi-restaurant, migrations, seed
│   ├── ChefOS.Back/                → outil chef (Interactive Server)
│   └── ChefOS.Vitrine/             → squelette seulement à l'étape 1
├── tests/
│   └── ChefOS.Tests/
└── docs/superpowers/specs/
```

| Projet | Rôle | Dépend de |
|--------|------|-----------|
| `ChefOS.Core` | Entités, `UnitConverter`, `CostCalculator`, `VatCalculator`. Aucune référence à EF Core ou ASP.NET. | — |
| `ChefOS.Infrastructure` | `ChefOsDbContext`, `ITenantContext`, filtres globaux, migrations, `DemoSeeder` | Core |
| `ChefOS.Back` | Identity, pages chef, sélecteur de restaurant | Core, Infrastructure |
| `ChefOS.Vitrine` | Page d'accueil vide, branchée sur la base | Core, Infrastructure |
| `ChefOS.Tests` | Tests unitaires et d'intégration | tous |

---

## 4. Modèle de données (étape 1)

### Multi-restaurant

- **`Restaurant`** : `Id`, `Name`, `Slug` (unique, utilisé par la vitrine), `Address`, `VatNumber`, `DefaultFoodVatRate` (12 %), `DefaultDrinkVatRate` (21 %), `FoodCostAlertThreshold` (35 %), `CreatedAt`.
- **`Membership`** : `UserId`, `RestaurantId`, `Role` (`Owner` | `Chef` | `Staff`). Un utilisateur peut appartenir à plusieurs restaurants.
- **`ITenantScoped`** : interface portée par toute entité métier (`RestaurantId`).

**Isolation** : `ITenantContext.CurrentRestaurantId` est résolu à partir de l'utilisateur connecté et du restaurant actif (stocké dans un cookie, vérifié contre `Membership`). Le `ChefOsDbContext` applique un `HasQueryFilter(e => e.RestaurantId == tenant.CurrentRestaurantId)` à chaque entité `ITenantScoped`. Il remplit aussi `RestaurantId` dans `SaveChanges` pour les ajouts, et lève une exception si une entité modifiée appartient à un autre restaurant.

### Ingrédients

- **`Ingredient`** (`ITenantScoped`)
  - `Name`, `Category`, `Allergens` (flags des 14 allergènes UE)
  - `BaseUnit` : `Gram` | `Milliliter` | `Piece`
  - `CurrentPricePerBaseUnit` (décimal, nullable : un ingrédient peut ne pas encore avoir de prix)
  - `YieldPercent` (1–100, 100 par défaut)
  - `AveragePieceWeightGrams` (nullable, pour convertir les pièces en grammes)
  - `SupplierId` (nullable), `IsArchived`
- **`IngredientPrice`** (`ITenantScoped`) : `IngredientId`, `Date`, `PricePerBaseUnit`, `SupplierId?`, `Source` (`Manual` | `Receipt`). Chaque changement de prix ajoute une ligne et met à jour `CurrentPricePerBaseUnit`.
- **`Supplier`** (`ITenantScoped`) : `Name`, `ContactName`, `Email`, `Phone`.

### Recettes

- **`Recipe`** (`ITenantScoped`)
  - `Name`, `Kind` : `Dish` (plat vendu) | `Preparation` (préparation de base)
  - Plat : `SellingPriceInclVat`, `VatRate`, `Category`
  - Préparation : `OutputQuantity` + `OutputUnit` (par exemple 2 l pour un fond)
  - `Instructions` (texte), `IsArchived`
- **`RecipeLine`** : `RecipeId`, **soit** `IngredientId` **soit** `SubRecipeId` (exactement un des deux, contrainte en base), `Quantity`, `Unit` (`g`, `kg`, `ml`, `cl`, `l`, `piece`).

### Entités prévues pour la suite (non créées à l'étape 1)

`SupplierAlias`, `Receipt`, `ReceiptLine` (étape 2) ; `Sale`, `WasteEntry`, `StockCount`, `Order` (étape 3) ; `Reservation`, `Payment` (étape 4).

---

## 5. Calcul des coûts (`ChefOS.Core`)

### Conversion d'unités (`UnitConverter`)

- Masse : `kg` → 1000 g. Volume : `l` → 1000 ml, `cl` → 10 ml.
- `piece` vers `Gram` : seulement si `AveragePieceWeightGrams` est renseigné.
- `Gram` vers `Piece` : seulement si `AveragePieceWeightGrams` est renseigné.
- Toute autre combinaison (masse ↔ volume, par exemple) renvoie une erreur `IncompatibleUnit`. Il n'y a pas de conversion par densité à l'étape 1.

### Formules

```
quantité en unité de base = UnitConverter(quantité, unité de la ligne, ingrédient)
coût d'une ligne ingrédient = quantité en unité de base ÷ (YieldPercent / 100) × CurrentPricePerBaseUnit
coût d'une ligne sous-prépa = quantité convertie dans l'OutputUnit de la prépa × (coût total de la prépa ÷ OutputQuantity)
coût de la recette          = somme des lignes
prix de vente HT            = SellingPriceInclVat ÷ (1 + VatRate)
food cost %                 = coût ÷ prix de vente HT
marge                       = prix de vente HT − coût
```

### Résultat (`CostResult`)

`TotalCost`, `IsComplete` (faux si un ingrédient n'a pas de prix ou si une unité est incompatible), `Issues` (liste de `CostIssue` : ingrédient ou ligne concernée et type de problème), puis, pour les plats seulement, `PriceExclVat`, `FoodCostPercent` et `Margin`. Un coût incomplet est calculé sur les lignes valides et affiché avec un avertissement, jamais présenté comme définitif.

### Boucles

Avant l'enregistrement d'une recette, un parcours en profondeur du graphe des sous-préparations détecte les cycles. Un cycle bloque l'enregistrement avec le message « *X* contient déjà *Y* (directement ou indirectement) ».

---

## 6. Écrans du back chef (étape 1)

| Route | Écran | Contenu |
|-------|-------|---------|
| `/Account/Register`, `/Account/Login` | Comptes | Pages Identity |
| `/onboarding` | Création du restaurant | Nom, adresse, n° TVA. Affiché si l'utilisateur n'a aucun restaurant ; il en devient `Owner`. |
| barre du haut | Sélecteur de restaurant | Liste des `Membership` de l'utilisateur |
| `/` | Tableau de bord | Nombre de recettes, food cost moyen des plats, liste « plats à surveiller » (au-dessus de `FoodCostAlertThreshold` ou au coût incomplet) |
| `/ingredients` | Liste des ingrédients | Filtre par catégorie, recherche, prix, rendement, archivés masqués par défaut |
| `/ingredients/{id}` | Fiche ingrédient | Édition des champs ; changer le prix ajoute une ligne `IngredientPrice` (`Manual`) ; mini-graphique de l'historique des prix ; recettes qui l'utilisent |
| `/recipes` | Liste des recettes | Onglets Plats / Préparations, coût, food cost %, pastille 🟢 (< 28 %) 🟠 (28 % – seuil) 🔴 (> seuil) ou ⚠️ (coût incomplet) |
| `/recipes/{id}` | Éditeur de recette | Lignes avec autocomplétion (ingrédients et préparations), quantité, unité, coût de chaque ligne ; total, food cost et marge recalculés en direct ; allergènes déduits des ingrédients et sous-préparations |
| `/suppliers` | Fournisseurs | Liste avec ajout, modification et suppression |
| `/settings` | Restaurant | Nom, slug, taux de TVA par défaut, seuil d'alerte |

L'invitation de membres d'équipe n'est **pas** dans l'étape 1 : seul le propriétaire utilise l'outil.

---

## 7. Règles et cas d'erreur

- **Ingrédient sans prix** : la recette affiche « ⚠️ coût incomplet » avec la liste des ingrédients concernés.
- **Unités incompatibles** : la ligne est marquée en erreur dans l'éditeur, avec une suggestion (« renseignez le poids moyen d'une pièce »).
- **Boucle de préparations** : l'enregistrement est refusé, avec un message explicite (§5).
- **Rendement** : il doit être compris entre 1 et 100 ; une valeur hors bornes est refusée par la validation.
- **Suppression d'un ingrédient ou d'une préparation utilisé(e)** : elle est refusée, avec la liste des recettes concernées ; l'élément peut à la place être archivé. Un élément archivé n'est plus proposé dans l'autocomplétion, mais les recettes existantes continuent de le chiffrer.
- **Accès à un autre restaurant** : le filtre global le rend invisible (404) ; changer de restaurant actif vers un restaurant sans `Membership` est refusé.
- **Slug** : il est unique au niveau mondial et généré à partir du nom, avec un suffixe numérique en cas de collision.

---

## 8. Données de démo (`DemoSeeder`)

En environnement `Development`, si la base est vide, le seeder crée :
- un utilisateur de démo et un restaurant « Le Petit Bistro » (slug `le-petit-bistro`) ;
- 3 fournisseurs ;
- environ 40 ingrédients belges réalistes, avec prix, rendements et allergènes ;
- environ 5 préparations (fond de veau, mayonnaise maison, sauce lapin à la bière, purée, pâte à gaufre) ;
- environ 10 plats (carbonnade flamande, boulets liégeois, vol-au-vent, moules-frites, américain préparé, croquettes de crevettes…), avec un historique de prix sur quelques ingrédients pour que les graphiques ne soient pas vides.

Les identifiants de démo sont écrits dans `appsettings.Development.json` et dans le README, jamais en dur dans le code.

---

## 9. Tests

- **Unitaires (`ChefOS.Core`)**
  - `UnitConverter` : toutes les conversions valides, et les erreurs pour les combinaisons incompatibles.
  - `CostCalculator` : rendement, préparations imbriquées sur deux niveaux, prix manquant (coût incomplet), unité incompatible, TVA (food cost calculé sur le HT), détection de cycle.
- **Intégration (Testcontainers PostgreSQL)**
  - Isolation : des données créées pour le restaurant A ne sont jamais renvoyées dans le contexte du restaurant B (lecture, recherche par id, modification).
  - `SaveChanges` remplit `RestaurantId` automatiquement.
  - Suppression refusée d'un ingrédient utilisé.
- **Démarrage** : le `DemoSeeder` s'exécute sans erreur sur une base vide, et chaque plat de démo a un coût complet.

---

## 10. Hors périmètre de l'étape 1

Scan de tickets, ventes, statistiques avancées, gaspillage, inventaires, commandes, contenu de la vitrine, réservations, paiements, invitation d'équipe, multi-langue (l'interface est en français), déploiement en production.

## 11. Définition de « terminé »

- `docker compose up -d` puis `dotnet run --project src/ChefOS.Back` démarre l'outil avec les données de démo.
- On peut créer un compte et un restaurant, ajouter des ingrédients et des recettes, et voir le coût, le food cost et la marge se mettre à jour en direct.
- Deux restaurants créés par deux comptes différents ne voient jamais les données l'un de l'autre.
- `dotnet test` passe entièrement.
