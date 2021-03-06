// Copyright 2020 The Tilt Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Generic;
using UnityEngine;

namespace TiltBrush {

// TODO: Could be slightly more vtx-efficient with a non-textured tube
// (no need to duplicate verts along seam)
// TODO: remove use of nRight, nSurface
class TubeBrush : GeometryBrush {
  const float TWOPI = 2 * Mathf.PI;

  const float kMinimumMoveMeters_PS = 5e-4f;
  const ushort kUpperBoundVertsPerKnot = 12;
  const float kBreakAngleScalar = 3.0f;
  const float kSolidAspectRatio = 0.2f;

  [SerializeField] float m_CapAspect = .8f;
  [SerializeField] ushort m_PointsInClosedCircle = 8;
  [SerializeField] bool m_EndCaps = true;
  [SerializeField] bool m_HardEdges = false;
  [SerializeField] protected UVStyle m_uvStyle = UVStyle.Distance;
  [SerializeField] protected ShapeModifier m_ShapeModifier = ShapeModifier.None;
  /// Specific to Taper shape modifier.
  [SerializeField] float m_TaperScalar = 1.0f;
  /// Specific to Petal shape modifier.
  [SerializeField] float m_PetalDisplacementAmt = 0.5f;
  [SerializeField] float m_PetalDisplacementExp = 3.0f;
  /// XXX - in my experience a higher multiplier actually makes
  /// the break LESS sensitive. Not more.
  ///
  /// Positive multiplier; 1.0 is standard, higher is more sensitive.
  [SerializeField] float m_BreakAngleMultiplier = 2;

  int m_VertsInClosedCircle;
  int m_VertsInCap;

  // Tube brush tracks per vert displacement directions for use with the vert post processing modifier
  List<Vector3> m_Displacements;

  protected enum UVStyle {
    Distance,
    Stretch
  };

  protected enum ShapeModifier {
    None,
    DoubleSidedTaper,
    Sin,
    Comet,
    Taper,
    Petal,
  };

  public TubeBrush() : this(true) { }

  public TubeBrush(bool bCanBatch)
    : base(bCanBatch: bCanBatch,
            upperBoundVertsPerKnot: kUpperBoundVertsPerKnot * 2,
            bDoubleSided: false) {
  }

  //
  // GeometryBrush API
  //

  protected override void InitBrush(BrushDescriptor desc, TrTransform localPointerXf) {
    base.InitBrush(desc, localPointerXf);
    m_geometry.Layout = GetVertexLayout(desc);

    if (m_ShapeModifier != ShapeModifier.None) {
      m_Displacements = new List<Vector3>();
    }

    if (m_HardEdges) {
      // Break verts along every seam
      m_VertsInClosedCircle = m_PointsInClosedCircle * 2;
    } else {
      // Only break a single vert to allow for proper UV unwrapping
      m_VertsInClosedCircle = m_PointsInClosedCircle + 1;
    }
    if (m_EndCaps) {
      m_VertsInCap = m_PointsInClosedCircle;
    } else {
      m_VertsInCap = 0;
    }

    // Start and end of circle are coincident, and need at least one more point.
    Debug.Assert(m_PointsInClosedCircle > 2);
    // Make sure the number of verts per knot are less than the upper bound
    Debug.Assert(m_VertsInClosedCircle <= kUpperBoundVertsPerKnot);
  }

  public override void ResetBrushForPreview(TrTransform unused) {
    base.ResetBrushForPreview(unused);
    if (m_Displacements != null) {
      m_Displacements.Clear();
    }
  }

  override public float GetSpawnInterval(float pressure01) {
    return m_Desc.m_SolidMinLengthMeters_PS * POINTER_TO_LOCAL * App.METERS_TO_UNITS +
        (PressuredSize(pressure01) * kSolidAspectRatio);
  }

  override protected void ControlPointsChanged(int iKnot0) {
    // Updating a control point affects geometry generated by previous knot
    // (if there is any). The HasGeometry check is not a micro-optimization:
    // it also keeps us from backing up past knot 0.
    int start = (m_knots[iKnot0 - 1].HasGeometry) ? iKnot0 - 1 : iKnot0;

    // Frames knots, determines how much geometry each knot should get.
    if (OnChanged_FrameKnots(start)) {
      // If we were notified that the beginning knot turned into a break, step back a knot.
      // Note that OnChanged_MakeGeometry requires our specified knot has a previous.
      start = Mathf.Max(1, start - 1);
    }
    OnChanged_MakeGeometry(start);
    ResizeGeometry();

    if (m_uvStyle == UVStyle.Stretch) {
      OnChanged_StretchUVs(start);
    }

    if (m_ShapeModifier != ShapeModifier.None) {
      OnChanged_ModifySilhouette(start);
    }
  }

  // Fills in any knot data needed for geometry generation.
  // Returns true if a strip break is detected on the initial knot.
  // - fill in length, nRight, nSurface, iVert, iTri
  // - calculate strip-break points
  bool OnChanged_FrameKnots(int iKnot0) {
    bool initialKnotContainsBreak = false;

    Knot prev = m_knots[iKnot0 - 1];
    for (int iKnot = iKnot0; iKnot < m_knots.Count; ++iKnot) {
      Knot cur = m_knots[iKnot];

      bool shouldBreak = false;

      Vector3 vMove = cur.smoothedPos - prev.smoothedPos;
      cur.length = vMove.magnitude;

      if (cur.length < kMinimumMoveMeters_PS * App.METERS_TO_UNITS * POINTER_TO_LOCAL) {
        shouldBreak = true;
      } else {
        Vector3 nTangent = vMove / cur.length;
        cur.qFrame = MathUtils.ComputeMinimalRotationFrame(
            nTangent, prev.Frame, cur.point.m_Orient);

        // More break checking; replicates previous logic
        // TODO: decompose into twist and swing; use different constraints
        // http://www.euclideanspace.com/maths/geometry/rotations/for/decomposition/
        if (prev.HasGeometry && !m_PreviewMode) {
          float fWidthHeightRatio = cur.length / PressuredSize(cur.smoothedPressure);
          float fBreakAngle = Mathf.Atan(fWidthHeightRatio) * Mathf.Rad2Deg
              * m_BreakAngleMultiplier;
          float angle = Quaternion.Angle(prev.qFrame, cur.qFrame);
          if (angle > fBreakAngle) {
            shouldBreak = true;
          }
        }
      }

      if (shouldBreak) {
        cur.qFrame = new Quaternion(0, 0, 0, 0);
        cur.nRight = cur.nSurface = Vector3.zero;
        if (iKnot == iKnot0) {
          initialKnotContainsBreak = true;
        }
      } else {
        cur.nRight = cur.qFrame * Vector3.right;
        cur.nSurface = cur.qFrame * Vector3.up;
      }

      // Just mark whether or not the strip is broken
      // tri/vert allocation will happen next pass
      cur.nTri = cur.nVert = (ushort)(shouldBreak ? 0 : 1);
      m_knots[iKnot] = cur;
      prev = cur;
    }

    return initialKnotContainsBreak;
  }

  // Textures are laid out so u goes along the strip,
  // and v goes across the strip (from left to right)
  void OnChanged_MakeGeometry(int iKnot0) {
    // Invariant: there is a previous knot.
    Knot prev = m_knots[iKnot0 - 1];
    for (int iKnot = iKnot0; iKnot < m_knots.Count; ++iKnot) {
      // Invariant: all of prev's geometry (if any) is correct and up-to-date.
      // Thus, there is no need to modify anything shared with prev.
      Knot cur = m_knots[iKnot];

      cur.iTri = prev.iTri + prev.nTri;
      cur.iVert = (ushort)(prev.iVert + prev.nVert);

      // Verts are: back cap, back circle, front circle, front cap
      // Back circle is shared with previous knot

      // Diagram:
      //
      //                    START KNOT              KNOT               KNOT
      //  <start cap>    <closed circle>     <closed circle>     <closed circle>     <end cap>
      //

      if (cur.HasGeometry) {
        cur.nVert = cur.nTri = 0;

        Vector3 rt = cur.qFrame * Vector3.right;
        Vector3 up = cur.qFrame * Vector3.up;
        Vector3 fwd = cur.qFrame * Vector3.forward;

        bool isStart = !prev.HasGeometry;
        bool isEnd = IsPenultimate(iKnot);

        // Verts, back half
        float u0, v0, v1;
        if (isStart) {
          float random01 = m_rng.In01(cur.iVert - 1);
          u0 = random01;

          int numV = m_Desc.m_TextureAtlasV;
          int iAtlas = (int)(random01 * 3331) % numV;
          v0 = (iAtlas) / (float)numV;
          v1 = (iAtlas + 1) / (float)numV;

          float prevSize = PressuredSize(prev.smoothedPressure);
          float prevRadius = prevSize / 2;
          float prevCircumference = TWOPI * prevRadius;
          float prevURate = m_Desc.m_TileRate / prevCircumference;
          if (m_EndCaps) {
            MakeCapVerts(
              ref cur, m_PointsInClosedCircle,
              prev.smoothedPos - fwd * prevRadius * m_CapAspect,
              prev.smoothedPos, prevRadius,
              u0, v0, v1, -prevURate,
              up, rt, fwd);
          }
          MakeClosedCircle(ref cur, prev.smoothedPos, prevRadius,
                            m_PointsInClosedCircle, up, rt, fwd, u0, v0, v1);
        } else {
          // Share some verts
          cur.iVert -= (ushort)(m_VertsInClosedCircle);
          cur.nVert += (ushort)(m_VertsInClosedCircle);

          // Start and end verts wrap differently between soft / hard edge geometry due to our topology
          // TO DO: Refactor this so that things... are sane.  Not sure exactly how to do that elegantly though.
          int iEdgeLoopStart = cur.iVert;
          int iEdgeLoopEnd = cur.iVert + m_VertsInClosedCircle - 1;
          if (m_HardEdges) {
            iEdgeLoopStart = cur.iVert + 1;
            iEdgeLoopEnd = cur.iVert;
          }

          if (m_Desc.m_TubeStoreRadiusInTexcoord0Z) {
            Vector3 u0v0 = m_geometry.m_Texcoord0.v3[iEdgeLoopStart];
            u0 = u0v0.x;
            v0 = u0v0.y;
            v1 = m_geometry.m_Texcoord0.v3[iEdgeLoopEnd].y;
          } else {
            Vector2 u0v0 = m_geometry.m_Texcoord0.v2[iEdgeLoopStart];
            u0 = u0v0.x;
            v0 = u0v0.y;
            v1 = m_geometry.m_Texcoord0.v2[iEdgeLoopEnd].y;
          }
        }

        // Verts, front half
        {
          float size = PressuredSize(cur.smoothedPressure);
          float radius = size / 2;
          float circumference = TWOPI * radius;
          float uRate = m_Desc.m_TileRate / circumference;

          float u1;
          u1 = u0 + cur.length * uRate;

          MakeClosedCircle(ref cur, cur.smoothedPos, radius,
                            m_PointsInClosedCircle, up, rt, fwd, u1, v0, v1);

          if (isEnd && m_EndCaps) {
            MakeCapVerts(
                ref cur, m_PointsInClosedCircle,
                cur.smoothedPos + fwd * radius * m_CapAspect,
                cur.smoothedPos, radius,
                u1, v0, v1, uRate,
                up, rt, fwd);
          }
        }

        // Tris
        // vert index of back circle.
        // If it is the start, then we will need to apply an additional vert offset
        // because we will be building triangles on the start cap prior to building
        // triangles on the end cap
        int BC = (isStart ? (int)m_VertsInCap : 0);
        //
        // vert index of front circle
        int FC = BC + m_VertsInClosedCircle;

        // Backcap
        if (isStart) {
          int CAP = 0;
          if (m_HardEdges) {
            for (int i = 0; i < m_VertsInCap; ++i) {
              // CAP + i is the start vert on the start cap
              // j is the first vert on the back circle
              // ii is the second vert on the back circle
              int j = i * 2 + 1;
              int ii = (j + 1) % (m_VertsInClosedCircle);
              AppendTri(ref cur, CAP + i, BC + j, BC + ii);
            }
          } else {
            for (int i = 0; i < m_VertsInCap; ++i) {
              // CAP + i is the start vert on the start cap
              // i is the first vert on the back circle
              // ii is the second vert on the back circle
              int ii = (i + 1);
              AppendTri(ref cur, CAP + i, BC + i, BC + ii);
            }
          }
        }

        // Cylinder
        if (m_HardEdges) {
          for (int i = 0; i < m_PointsInClosedCircle; i += 1) {
            int j = i * 2 + 1;
            int ii = (j + 1) % (m_VertsInClosedCircle);
            AppendTri(ref cur, BC + j, FC + j, BC + ii);
            AppendTri(ref cur, BC + ii, FC + j, FC + ii);
          }
        } else {
          for (int i = 0; i < m_VertsInClosedCircle - 1; ++i) {
            int ii = (i + 1);
            AppendTri(ref cur, BC + i, FC + i, BC + ii);
            AppendTri(ref cur, BC + ii, FC + i, FC + ii);
          }
        }

        // Front cap
        if (isEnd) {
          int CAP = FC + m_VertsInClosedCircle;
          if (m_HardEdges) {
            for (int i = 0; i < m_VertsInCap; ++i) {
              // CAP + i is the start vert on the end cap
              // ii is the first vert on the front circle
              // j is the second vert on the front circle
              int j = i * 2 + 1;
              int ii = (j + 1) % (m_VertsInClosedCircle);
              AppendTri(ref cur, CAP + i, FC + ii, FC + j);
            }
          } else {
            for (int i = 0; i < m_VertsInCap; ++i) {
              // CAP + i is the start vert on the end cap
              // i is the first vert on the front circle
              // ii is the second vert on the front circle
              int ii = (i + 1);
              AppendTri(ref cur, CAP + i, FC + ii, FC + i);
            }
          }
        }
      }
      m_knots[iKnot] = cur;
      prev = cur;
    }
  }

  // TODO: Set correct UVs on end caps. Right now this runs over all knots in the brush,
  // but the start knot and end knot have additional verts in the case of the end caps.
  // Those need to be special cased.
  void OnChanged_StretchUVs(int iChangedKnot) {
    // Back up knot to the start of the segment
    // Invariant: knot 0 never has geometry
    int knotSegmentStart = iChangedKnot;
    while (m_knots[knotSegmentStart - 1].HasGeometry) {
      knotSegmentStart -= 1;
    }

    // Modify this segment, and if it doesn't end at the stroke end, advance to the next segment.
    while (true) {
      int knotPastSegmentEnd = ModifyStretchUVsOfSegment(knotSegmentStart);
      if (knotPastSegmentEnd >= m_knots.Count) {
        break;
      }
      knotSegmentStart = knotPastSegmentEnd;
    }
  }

  int ModifyStretchUVsOfSegment(int initialSegmentKnot) {
    // Find length and end knot of segment
    int totalNumKnots = 0;
    int endSegmentKnot = initialSegmentKnot;
    for (; endSegmentKnot < m_knots.Count; ++endSegmentKnot) {
      Knot cur = m_knots[endSegmentKnot];
      if (!cur.HasGeometry) {
        break;
      }
      totalNumKnots++;
    }

    //Iterate over the knots in this segment
    int numKnots = 0;
    for (int iKnot = initialSegmentKnot; iKnot < endSegmentKnot; ++iKnot) {
      Knot cur = m_knots[iKnot];
      float u = (float)numKnots / (float)totalNumKnots;
      int numVerts = cur.nVert;
      for (int i = 0; i < numVerts; i++) {
        int vert = (cur.iVert + i);
        if (m_Desc.m_TubeStoreRadiusInTexcoord0Z) {
          var tmp = m_geometry.m_Texcoord0.v3[vert];
          tmp.x = u;
          m_geometry.m_Texcoord0.v3[vert] = tmp;
        } else {
          var tmp = m_geometry.m_Texcoord0.v2[vert];
          tmp.x = u;
          m_geometry.m_Texcoord0.v2[vert] = tmp;
        }
      }
      numKnots++;
    }

    return endSegmentKnot + 1;
  }

  internal class LoftedProfile {
    // The number of knots at the start and end of the stroke.
    const int kNumEndKnots = 5;

    // The minimum number of knots required to draw anything.
    const int kMinKnotCount = 3;

    float partialProgress;
    int knotCount;

    public LoftedProfile(GeometryBrush brush,
                         int firstKnotIndex, int lastKnotIndex,
                         float totalLength, float lastLength,
                         List<Knot> knots) {
      // Compute the partial progress to emitting the next knot, this is used to shape the verts
      // by knot continuously (knots are intrinsicly discrete) without popping as new knots are
      // added.
      partialProgress =
          Mathf.Clamp01(lastLength / brush.GetSpawnInterval(knots[lastKnotIndex].smoothedPressure));

      knotCount = lastKnotIndex - firstKnotIndex + 1;
    }

    public float ComputeCurve(int iKnot,
                              int firstKnotIndex,
                              int lastKnotIndex,
                              float t,
                              float tPrev) {

      // Not enough knots to make a meaningful shape.
      if (knotCount < kMinKnotCount) {
        return 0;
      }
      
      // The leading and trailing knot count.
      int halfCount = Mathf.CeilToInt(Mathf.Min(kNumEndKnots, knotCount / 2.0f));
      int nextHalfCount = Mathf.CeilToInt(Mathf.Min(kNumEndKnots, (knotCount + 1) / 2.0f));

      // This is the segment-relative index into the curve, where iKnot is the absolute offset in
      // all knots for all segments.
      int localIndex = iKnot - firstKnotIndex;

      // The index starting from the tail, i.e. the last knot has reverseIndex = 0.
      int reverseIndex = knotCount - localIndex - 1;
      int nextReverseIndex = (knotCount + 1) - localIndex - 1;

      float curValue = 1;
      float nextValue = 1;

      // Note that the current and next knots must be computed separately because when the cuve is
      // extremely small, knots will transition from head to tail and the half count will change
      // as knots are added until there are kNumEndKnots * 2 total knots.

      // Compute the knot position at given the current curve.
      if (localIndex < halfCount) {
        // The head of the curve.
        curValue = localIndex / (halfCount - 1f);
      } else if (reverseIndex < halfCount) {
        // The tail of the curve.
        curValue = Mathf.Max(0f, reverseIndex - 1f) / Mathf.Max(1f, halfCount - 1f);
      }

      // Compute the knot position for the next curve, immediately after we add a new knot.
      if (localIndex < nextHalfCount) {
        // The head of the curve.
        nextValue = localIndex / (nextHalfCount - 1f);
      } else if (nextReverseIndex < nextHalfCount) {
        // The tail of the curve.
        nextValue = Mathf.Max(0f, nextReverseIndex - 1f) / Mathf.Max(1f, nextHalfCount - 1f);
      }

      // TODO: this is a gross hack to account for the fact that curValue is too small,
      // this ultra magical scaling factor fixes some undulation at the head of the curve. The
      // correct fix requires refactoring the curValue above when reverseIndex < halfCount.
      curValue = Mathf.Lerp(curValue, nextValue, 0.185f);

      // Smoothly interpolate between the previous curve and the next curve as the next knot comes
      // into existence. This is required to eliminate pops as each knot is created.
      curValue = Mathf.Lerp(curValue, nextValue, partialProgress);

      // Finally attenuate the curve amplitude when there are a small number of knots, this hides
      // the ugly transtion from "not enough knots" to the steady state.
      float atten = Mathf.Clamp01(
          (knotCount - kMinKnotCount + partialProgress) / (kNumEndKnots * 2f - kMinKnotCount));
      curValue *= atten;

      return Mathf.Clamp01(curValue);
    }
  }

  // Post process vert geometry per segment.
  // Can be leveraged for more interesting shapes
  void OnChanged_ModifySilhouette(int iChangedKnot) {
    // Back up knot to the start of the segment
    // Invariant: knot 0 never has geometry
    int knotSegmentStart = iChangedKnot;
    while (m_knots[knotSegmentStart - 1].HasGeometry) {
      knotSegmentStart -= 1;
    }

    // Modify this segment, and if it doesn't end at the stroke end, advance to the next segment.
    while (true) {
      int knotPastSegmentEnd = ModifySilhouetteOfSegment(knotSegmentStart);
      if (knotPastSegmentEnd >= m_knots.Count) {
        break;
      }
      knotSegmentStart = knotPastSegmentEnd;
    }
  }

  // Returns the next knot after the end of segment.
  int ModifySilhouetteOfSegment(int initialSegmentKnot) {
    // Find the last knot and how long this segment is.
    float totalLength = 0;
    int endSegmentKnot = initialSegmentKnot;
    for (; endSegmentKnot < m_knots.Count; ++endSegmentKnot) {
      Knot cur = m_knots[endSegmentKnot];
      if (!cur.HasGeometry) {
        break;
      }
      totalLength += cur.length;
    }

    // Specific to petal shape.
    float petalAmtCacheValue = m_PetalDisplacementAmt * POINTER_TO_LOCAL * m_BaseSize_PS;

    // Specific to the double taper shape.
    LoftedProfile lofted = null;
    if (m_ShapeModifier == ShapeModifier.DoubleSidedTaper) {
      float lastLength = DistanceFromKnot(
          Mathf.Max(0, endSegmentKnot - 2), m_knots[endSegmentKnot - 1].point.m_Pos);

      // "endSegmentKnot - 1" because the last knot does not generate geometry.
      lofted = new LoftedProfile(this, initialSegmentKnot, endSegmentKnot - 1, totalLength,
          lastLength, m_knots);
    }

    // Iterate over the knots in this segment
    float distance = 0;
    for (int iKnot = initialSegmentKnot; iKnot < endSegmentKnot; ++iKnot) {
      Knot cur = m_knots[iKnot];
      Knot prev = m_knots[iKnot - 1];
      bool isStart = !prev.HasGeometry;
      bool isEnd = IsPenultimate(iKnot);

      // The curve parameter t never goes to zero, because geometry knots always have non-zero
      // length, so tPrev represents a prameter that goes from [0, 1), rather than (0, 1].
      float tPrev = distance / totalLength;
      distance += cur.length;
      float t = distance / totalLength;

      int numVerts = cur.nVert;
      for (int i = 0; i < numVerts; i++) {
        int vert = (cur.iVert + i);
        float radius = PressuredSize(cur.smoothedPressure) / 2.0f;
        Vector3 dir = m_Displacements[vert];

        // skip start/end cap verts
        if (m_EndCaps) {
          if (isStart && (i < m_VertsInCap)) {
            continue;
          }
          // XXX: This needs more attention.  Modifiers don't always play nicely with
          // geo that only has start/end caps.
          bool bEndCapGeometryIsComplete = (m_VertsInClosedCircle * 2 + m_VertsInCap) == numVerts;
          if (isEnd && bEndCapGeometryIsComplete && (i >= m_VertsInClosedCircle * 2)) {
            continue;
          }
        }

        float curve = 0;
        Vector3 offset = Vector3.zero;

        switch (m_ShapeModifier) {
        // Double Sided Taper (i.e. Jeremy's Lofted Brush)
        case ShapeModifier.DoubleSidedTaper:
          // "endSegmentKnot - 1" because the last knot does not generate geometry.
          curve = lofted.ComputeCurve(iKnot, initialSegmentKnot, endSegmentKnot - 1, t, tPrev);
          break;
        // Sin curve
        case ShapeModifier.Sin:
          curve = Mathf.Abs(Mathf.Sin(t * Mathf.PI));
          break;
        // Taper for fire
        case ShapeModifier.Comet:
          curve = Mathf.Sin(t * 1.5f + 1.55f);
          break;
        // Tapers to a point
        case ShapeModifier.Taper:
          curve = m_TaperScalar * (1 - t);
          break;
        case ShapeModifier.Petal:
          curve = Mathf.Abs(Mathf.Sin(t * Mathf.PI));
          float displacement = Mathf.Pow(t, m_PetalDisplacementExp);
          offset = m_geometry.m_Normals[vert] * displacement * petalAmtCacheValue *
              cur.smoothedPressure;
          break;
        }
        m_geometry.m_Vertices[vert] = offset + cur.smoothedPos + radius * dir * curve;
      }
    }

    return endSegmentKnot + 1;
  }

  // Cap verts always have the same # of points & verts
  void MakeCapVerts(
      ref Knot k, int numPoints,
      Vector3 tip, Vector3 circleCenter, float radius,
      float u0, float v0, float v1, float uRate,
      Vector3 up, Vector3 rt, Vector3 fwd) {
    // Length of diagonal between circle and tip
    float diagonal = ((circleCenter + up * radius) - tip).magnitude;
    float u = u0 + uRate * diagonal;

    Vector3 fwdNormal = Mathf.Sign(Vector3.Dot(tip - circleCenter, fwd)) * fwd;
    for (int i = 0; i < numPoints; ++i) {
      // Endcap vert n tangent points halfway between circle verts n and (n+1)
      float t = (i + .5f) / numPoints;
      float theta = TWOPI * t;
      Vector3 tan = -Mathf.Cos(theta) * up + -Mathf.Sin(theta) * rt;
      Vector2 uv = new Vector2(u, Mathf.Lerp(v0, v1, t));

      Vector3 normal = fwdNormal;
      if (m_HardEdges) {
        // For hard edges, use the same normal calculations as the other closed circles.
        normal = -Mathf.Cos(theta) * up + -Mathf.Sin(theta) * rt;
      }

      // Note that for the purpose of displacement in the shader,
      // radius is zero on the end point verts.
      //
      // Additionally, be aware that here "radius" is packed into a vertex channel but
      // does not actually have anything to do with the radius of the created geometry.
      //
      AppendVert(ref k, tip, normal.normalized, m_Color, tan, uv, /*radius*/ 0);
      AppendDisplacement(ref k, fwdNormal);
    }
  }

  void MakeClosedCircle(
      ref Knot k, Vector3 center, float radius, int numPoints,
      Vector3 up, Vector3 rt, Vector3 fwd,
      float u, float v0, float v1) {
    if (m_HardEdges) {
      MakeClosedCircleHardEdges(ref k, center, radius, numPoints, up, rt, fwd, u, v0, v1);
    } else {
      MakeClosedCircleSoftEdges(ref k, center, radius, numPoints, up, rt, fwd, u, v0, v1);
    }
  }

  // Soft edge circle rings have one more vertex than the number of points.  verts = points + 1
  // The additional vertex is only used to wrap UV's properly around a ring.
  // Parameters "up" and "rt" are assumed to be normalized.
  void MakeClosedCircleSoftEdges(
      ref Knot k, Vector3 center, float radius, int numPoints,
      Vector3 up, Vector3 rt, Vector3 fwd,
      float u, float v0, float v1) {
    // When facing down the tangent, circle verts should go clockwise
    // We'd like the seam to be on the bottom
    int numVerts = numPoints + 1;
	for (int i = 0; i < numVerts; ++i) {
      float t = (float)i / (numVerts-1);
      // Ensure that the first and last verts are exactly coincident
      float theta = (t == 1) ? 0 : TWOPI * t;
      Vector2 uv = new Vector2(u, Mathf.Lerp(v0, v1, t));
      Vector3 off = -Mathf.Cos(theta) * up + -Mathf.Sin(theta) * rt;

      AppendVert(ref k, center + radius * off, off, m_Color, fwd, uv, radius);
      AppendDisplacement(ref k, off.normalized);
    }
  }

  // Hard edge circle rings have double the vertices than the number of points.  verts = points * 2.
  // Parameters "up" and "rt" are assumed to be normalized.
  void MakeClosedCircleHardEdges(
      ref Knot k, Vector3 center, float radius, int numPoints,
      Vector3 up, Vector3 rt, Vector3 fwd,
      float u, float v0, float v1) {
    // When facing down the tangent, circle verts should go clockwise
    // We'd like the seam to be on the bottom
    float? lastTheta = null;

    for (int i = 0; i < numPoints; ++i) {
      float t = (float)i / (numPoints);

      // Ensure that the first and last verts are exactly coincident
      float theta = (t == 0) ? 0 : TWOPI * t;
      if (!lastTheta.HasValue) {
        lastTheta = theta - (TWOPI / numPoints);
      }

      Vector3 tan = -Mathf.Cos(theta) * up + -Mathf.Sin(theta) * rt;

      float dTheta = TWOPI / numPoints * .5f;
      Vector3 off1 = -Mathf.Cos(theta) * up + -Mathf.Sin(theta) * rt;
      Vector3 nCur = -Mathf.Cos(theta + dTheta) * up + -Mathf.Sin(theta + dTheta) * rt;
      Vector3 nPrev = -Mathf.Cos(theta - dTheta) * up + -Mathf.Sin(theta - dTheta) * rt;

      lastTheta = theta;

      // Calculate V's for hard edges.
      int prevFace = (i + (numPoints - 1)) % numPoints;
      float v = Mathf.Lerp(v0, v1, (float)prevFace / (float)(numPoints) + (1.0f / numPoints));
      Vector2 uv = new Vector2(u, v);

      AppendVert(ref k, center + radius * off1, nPrev, m_Color, tan, uv, radius);
      AppendDisplacement(ref k, off1);

      int currentFace = i;
      v = Mathf.Lerp(v0, v1, (float)currentFace / (float)(numPoints));
      uv = new Vector2(u, v);

      AppendVert(ref k, center + radius * off1, nCur, m_Color, tan, uv, radius);
      AppendDisplacement(ref k, off1);
    }
  }

  override public GeometryPool.VertexLayout GetVertexLayout(BrushDescriptor desc) {
      bool radiusInZ = desc.m_TubeStoreRadiusInTexcoord0Z;
    return new GeometryPool.VertexLayout {
      bUseColors = true,
      bUseNormals = true,
      bUseTangents = true,
      uv0Size = radiusInZ ? 3 : 2,
      uv0Semantic = radiusInZ ? GeometryPool.Semantic.XyIsUvZIsDistance : GeometryPool.Semantic.XyIsUv,
      uv1Size = 0
    };
  }

  void AppendDisplacement(ref Knot k, Vector3 v) {
    if (m_ShapeModifier == ShapeModifier.None) {
      return;
    }
    int i = k.iVert + k.nVert - 1;
    if (i >= m_Displacements.Count) {
      m_Displacements.Add(v);
    } else {
      m_Displacements[i] = v;
    }
  }

  /// Resizes arrays if necessary, appends data, mutates knot's vtx count. The
  /// incoming normal n should be normalized.
  void AppendVert(ref Knot k, Vector3 v, Vector3 n, Color32 c,
      Vector3 tan, Vector2 uv, float radius) {
    int i = k.iVert + k.nVert++;
    Vector3 uv3 = new Vector3(uv.x, uv.y, radius);
    Vector4 tan4 = tan;
    // Making sure Tangent w is 1.0
    tan4.w = 1.0f;

    if (i == m_geometry.m_Vertices.Count) {
      m_geometry.m_Vertices .Add(v);
      m_geometry.m_Normals  .Add(n);
      m_geometry.m_Colors   .Add(c);
      if (m_Desc.m_TubeStoreRadiusInTexcoord0Z) {
        m_geometry.m_Texcoord0.v3.Add(uv3);
      } else {
        m_geometry.m_Texcoord0.v2.Add(uv);
      }
      m_geometry.m_Tangents .Add(tan4);
    } else {
      m_geometry.m_Vertices[i] = v;
      m_geometry.m_Normals[i]  = n;
      m_geometry.m_Colors[i]   = c;
      if (m_Desc.m_TubeStoreRadiusInTexcoord0Z) {
        m_geometry.m_Texcoord0.v3[i] = uv3;
      } else {
        m_geometry.m_Texcoord0.v2[i] = uv;
      }
      m_geometry.m_Tangents[i] = tan4;
    }
  }

  void AppendTri(ref Knot k, int t0, int t1, int t2) {
    int i = (k.iTri + k.nTri++) * 3;
    if (i == m_geometry.m_Tris.Count) {
      m_geometry.m_Tris.Add(k.iVert + t0);
      m_geometry.m_Tris.Add(k.iVert + t1);
      m_geometry.m_Tris.Add(k.iVert + t2);
    } else {
      m_geometry.m_Tris[i + 0] = k.iVert + t0;
      m_geometry.m_Tris[i + 1] = k.iVert + t1;
      m_geometry.m_Tris[i + 2] = k.iVert + t2;
    }
  }

  bool IsPenultimate(int iKnot) {
    return (iKnot+1 == m_knots.Count || !m_knots[iKnot+1].HasGeometry);
  }
}
}  // namespace TiltBrush
