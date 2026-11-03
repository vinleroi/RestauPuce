# ChefOS — Étape 3 : Ventes, pertes et statistiques

**Date** : 2026-11-03
**Statut** : design rédigé par Claude. L'utilisateur a demandé de passer directement au code, donc **tous les choix ci-dessous sont des choix de Claude**.
**Base** : branche `feature/etape-3-ventes-stats`, créée depuis `feature/etape-2-tickets`.

## 1. Intention

Le chef doit voir, pour une période (semaine, mois, période libre), **ce qu'il gagne et ce qu'il perd** :
- le chiffre d'affaires HT et la marge par plat ;
- les plats qui rapportent et ceux qui coûtent (menu engineering) ;
- le coût du gaspillage déclaré ;
- l'écart entre la consommation théorique (ventes × recettes) et la consommation réelle mesurée par inventaire. Cet écart, c'est l'argent qui disparaît : vol, sur-portionnement, pertes non déclarées.

**Anti-objectifs**
- On ne mélange pas sans le dire les ventes saisies à la main (caisse externe) et les commandes enregistrées dans ChefOS : la source est toujours indiquée.
- Aucun chiffre n'est présenté comme exact quand il repose sur une recette incomplète : l'indicateur « incomplet » apparaît partout.
- Un restaurant ne voit jamais les données d'un autre.

## 2. Sources de ventes

**Ventes saisies** (`SalesEntry`) : ventes faites hors ChefOS, recopiées depuis la caisse.
- Saisie par plat sur une grille semaine × plats : une colonne par jour, ou une seule colonne « Total semaine ».
- Une ligne contient le plat, la date (le jour, ou le lundi pour un total hebdomadaire), le service (`Journee | Midi | Soir`), la quantité et l'indicateur `IsWeeklyTotal`. Le prix TTC et la TVA en vigueur au moment de la saisie sont figés dans la ligne.
- La saisie d'une cellule remplace la valeur existante pour le même (plat, date, service, granularité).

**Commandes** (`Order`) : prises dans ChefOS (salle, à emporter, téléphone ; la vitrine viendra à l'étape 4).
- Le statut suit le cycle `Nouvelle → EnPreparation → Prete → Servie`, avec la possibilité de passer en `Annulee`.
- Les lignes (plat, quantité, prix TTC et TVA figés, note) sont modifiables tant que la commande n'est ni servie ni annulée.
- Les commandes non annulées comptent comme ventes à la date de création (jour UTC).

Les statistiques additionnent les deux sources et affichent la part de chacune.

## 3. Pertes et inventaires

**Gaspillage** (`WasteEntry`) :
- Une perte porte soit sur un ingrédient (quantité + unité), soit sur une recette : une préparation (quantité + unité) ou un plat (en portions).
- On indique la date, la raison (`Perime | Casse | Erreur | Surproduction | Autre`) et une note.
- La perte est valorisée au prix en vigueur à sa date, via le coût de revient pour une recette.

**Inventaire** (`StockCount`) :
- Comptage daté des ingrédients, avec une quantité par ingrédient saisie dans une unité au choix et convertie en unité de base. Le statut passe de `Brouillon` à `Cloture`.
- Seuls les inventaires clôturés servent aux calculs. Un inventaire clôturé est en lecture seule.

## 4. Calculs (choix de Claude)

- **Prix en vigueur à une date** : la dernière entrée d'historique dont la date est inférieure ou égale à la fin de cette journée UTC. À défaut, le prix courant de l'ingrédient. Les coûts d'une période utilisent les prix en vigueur à la **fin** de la période.
- **Coût d'un plat vendu** : le coût de revient de la recette (étape 1, avec rendement et sous-préparations), calculé avec les prix en vigueur. Le CA HT d'une vente vaut quantité × prix TTC figé ÷ (1 + TVA figée).
- **Rentabilité par plat** : quantités vendues, CA HT, coût total, marge, food cost %, et un indicateur « coût incomplet ».
- **Menu engineering** (Kasavana & Smith), uniquement sur les plats vendus et chiffrés de façon complète :
  - popularité forte si la part du plat ≥ 70 % × (1 / nombre de plats) ;
  - marge forte si la marge unitaire ≥ la marge unitaire moyenne pondérée ;
  - classes : `Etoile` (forte, forte), `VacheALait` (populaire, faible marge), `Enigme` (peu populaire, forte marge), `PoidsMort` (faible, faible).
- **Consommation théorique** :
  - Les ventes de la période sont décomposées récursivement en ingrédients, en quantités **brutes** (quantité nette ÷ rendement) et en unité de base. Une sous-préparation compte au prorata de sa quantité produite.
  - On y ajoute les pertes déclarées, décomposées de la même façon.
- **Écart d'inventaire**, entre deux inventaires clôturés A (début) et B (fin) :
  - consommation réelle = stock A + achats (lignes de tickets appliqués datés entre A exclu et B inclus, en quantité de base) − stock B ;
  - écart = réelle − théorique (ventes + pertes) entre ces dates, valorisé aux prix en vigueur à la date de B ;
  - un écart positif est une perte inexpliquée ;
  - la liste est triée par écart en euros décroissant.
  Limite assumée : les achats saisis à la main (sans ticket) n'ont pas de quantité, et ne sont donc pas comptés. L'écran l'indique.
- **Food cost** : le food cost théorique vaut coût des ventes ÷ CA HT. Le food cost réel, disponible seulement si la période est bornée par deux inventaires clôturés, vaut (coût de la consommation réelle) ÷ CA HT.

## 5. Écrans (back chef)

- `/sales` : grille de la semaine (sélecteur de semaine), avec une ligne par plat non archivé et sept colonnes de jour plus une colonne « Total semaine ». Un commutateur affiche le détail Midi/Soir. Totaux par jour et par plat, CA HT de la semaine.
- `/orders` : liste du jour par statut, en colonnes de type Kanban, avec « Nouvelle commande » (source, table ou nom, lignes, note). Boutons pour avancer le statut et pour annuler.
- `/waste` : liste filtrable par période, ajout et suppression, coût de chaque perte, total de la période.
- `/inventory` : liste des inventaires. `/inventory/{id}` : feuille de comptage avec tous les ingrédients non archivés groupés par catégorie, une quantité et une unité par ingrédient, et un bouton « Clôturer ».
- `/stats` : sélecteur de période (cette semaine, semaine dernière, ce mois, mois dernier, ou personnalisée) :
  - KPI : CA HT, coût théorique, marge brute, food cost théorique, pertes, food cost réel quand il est disponible ;
  - graphique du CA par jour ;
  - tableau de rentabilité par plat ;
  - matrice menu engineering ;
  - pertes par raison ;
  - écart d'inventaire si deux inventaires clôturés encadrent la période (sinon, un message explique comment l'obtenir).
- Tableau de bord : ajout des KPI « CA HT (7 jours) » et « Marge brute (7 jours) ».
- Données de démo du Petit Bistro : 8 semaines de ventes saisies, quelques commandes, quelques pertes et 2 inventaires clôturés encadrant des tickets appliqués, pour que `/stats` soit vivant.

## 6. Hors périmètre

Stock théorique en continu, alertes de réapprovisionnement, export comptable, statistiques par serveur ou par table, prévisions, vitrine et paiement (étape 4).

## 7. Terminé quand

- Avec le compte de démo, `/stats` sur les 4 dernières semaines montre le CA, les marges par plat, la matrice menu engineering, les pertes et un écart d'inventaire.
- On peut saisir des ventes, prendre et faire avancer une commande, déclarer une perte et réaliser un inventaire.
- `dotnet test` passe et le build ne produit aucun avertissement.
