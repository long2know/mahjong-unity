import type { Place, ThingType, GameType } from '../types';
import type { Thing } from '../thing';

export interface Render {
  type: ThingType;
  thingIndex: number;
  hidden?: boolean;
  drawn?: boolean;
  place: Place;
  selected: boolean;
  hovered: boolean;
  held: boolean;
  temporary: boolean;
  bottom: boolean;
  highlighted: boolean;
}

export interface RendererSink {
  setVariant(gameType: GameType): void;
  replaceThings(things: Map<number, Thing>): void;
  replaceShadows(places: Place[]): void;
  updateThings(things: Render[]): void;
  updateDropShadows(places: Place[]): void;
  updateScores(scores: Array<number | null>): void;
  highlightIntensity: number;
}

function copyPlace(place: Place): Place {
  return { position: place.position.clone(), rotation: place.rotation.clone(), size: place.size.clone(), scale: place.scale };
}

/** World writes only data. The adapter reads it after the entire model update. */
export class BufferedRendererSink implements RendererSink {
  highlightIntensity = 0;
  generation = 0;
  renders: readonly Render[] = [];
  scores: readonly (number | null)[] = [];
  shadows: readonly Place[] = [];
  dropShadows: readonly Place[] = [];
  private variant: GameType | null = null;

  setVariant(gameType: GameType): void {
    if (gameType !== this.variant) { this.variant = gameType; this.generation++; }
  }
  replaceThings(_things: Map<number, Thing>): void { this.generation++; this.renders = []; }
  replaceShadows(places: Place[]): void { this.shadows = places.map(copyPlace); }
  updateThings(things: Render[]): void { this.renders = things.map(row => ({ ...row, place: copyPlace(row.place) })); }
  updateDropShadows(places: Place[]): void { this.dropShadows = places.map(copyPlace); }
  updateScores(scores: Array<number | null>): void { this.scores = scores.slice(); }
  clear(): void { this.renders = []; this.scores = []; this.shadows = []; this.dropShadows = []; this.highlightIntensity = 0; }
}
