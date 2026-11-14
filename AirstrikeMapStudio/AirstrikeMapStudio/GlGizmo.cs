using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public enum GizmoPart
    {
        None,
        Center,
        AxisX,
        AxisY,
        Rotation
    }

    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Interactive 3D Transform Gizmo
        // ═════════════════════════════════════════════════════════════════

        [DllImport("opengl32.dll")]
        private static extern void glLineWidth(float width);

        private const uint GL_LINE_LOOP = 0x0002;

        public bool ShowGizmo = true;

        private GizmoPart gizmoHover = GizmoPart.None;
        private GizmoPart gizmoActive = GizmoPart.None;
        private Point gizmoStartMouse;
        private int gizmoStartObjX;
        private int gizmoStartObjY;
        private byte gizmoStartFacing;
        private int gizmoCurrentX;
        private int gizmoCurrentY;
        private byte gizmoCurrentFacing;
        private uint gizmoDragList = 0;
        private bool gizmoMoved = false;

        private bool IsGizmoEligible =>
            ShowGizmo
            && SelectedIndex >= 0
            && SelectedIndex < map.Objects.Count
            && SelectedObjects.Length == 1
            && !TerrainMode
            && !TileMode
            && !EditRoute
            && !PlacementPending
            && !objectDrag
            && !boxDragging;

        private Point3D GetGizmoWorldCenter()
        {
            if (SelectedIndex < 0 || SelectedIndex >= map.Objects.Count)
                return new Point3D();

            var p = map.Objects[SelectedIndex];
            int gx = (gizmoActive != GizmoPart.None ? gizmoCurrentX : p.X);
            int gy = (gizmoActive != GizmoPart.None ? gizmoCurrentY : p.Y);

            double wx = MapWorld.SpawnX(gx);
            double wy = MapWorld.SpawnY(gy);
            double baseZ = ObjectZ(p, gx, gy);

            var b = SelectionBounds();
            double wz = (!b.IsEmpty && b.SizeZ > 0)
                ? Math.Max(baseZ, b.Z + b.SizeZ * 0.3) + 2.0
                : baseZ + 5.0;

            return new Point3D(wx, wy, wz);
        }

        private double GetGizmoScale(Point3D center)
        {
            int vh = Math.Max(1, Math.Min(ClientSize.Height, ClientSize.Width * 3 / 4));
            double a = Heading * Math.PI / 180;
            double p = Pitch * Math.PI / 180;
            var camOffset = new Vector3D(
                Math.Cos(a) * Math.Cos(p),
                Math.Sin(a) * Math.Cos(p),
                Math.Sin(p));
            var camOrigin = new Point3D(CenterX * 40, CenterY * 40, 0) + camOffset * Distance;
            double dist = (center - camOrigin).Length;
            if (dist < 10) dist = Distance;

            double targetPixels = 70.0;
            double worldSize = (targetPixels * dist * 2.0 * VerticalHalfFovTan) / vh;
            return Math.Max(15.0, Math.Min(2500.0, worldSize));
        }

        private void DrawGizmo()
        {
            if (!IsGizmoEligible) return;

            Point3D center = GetGizmoWorldCenter();
            double size = GetGizmoScale(center);
            double ringRadius = size * 0.85;
            double arrowLen = size * 0.95;
            double headLen = size * 0.22;
            double headWidth = size * 0.08;

            var p = map.Objects[SelectedIndex];
            byte facing = (gizmoActive == GizmoPart.Rotation ? gizmoCurrentFacing : p.Setting6);

            glDisable(GL_TEXTURE_2D);
            glDisable(GL_FOG);
            glDisable(GL_DEPTH_TEST);
            glEnable(GL_BLEND);
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

            // 1. Center Handle (Diamond)
            bool centerHilite = (gizmoActive == GizmoPart.Center || gizmoHover == GizmoPart.Center);
            float cd = (float)(size * 0.16);
            if (centerHilite)
                glColor4f(1.0f, 0.95f, 0.1f, 0.85f);
            else
                glColor4f(0.9f, 0.9f, 0.95f, 0.45f);

            glBegin(GL_QUADS);
            glVertex3f((float)center.X, (float)(center.Y - cd), (float)center.Z);
            glVertex3f((float)(center.X + cd), (float)center.Y, (float)center.Z);
            glVertex3f((float)center.X, (float)(center.Y + cd), (float)center.Z);
            glVertex3f((float)(center.X - cd), (float)center.Y, (float)center.Z);
            glEnd();

            glLineWidth(2.0f);
            if (centerHilite)
                glColor4f(1.0f, 1.0f, 1.0f, 1.0f);
            else
                glColor4f(0.8f, 0.8f, 0.9f, 0.9f);

            glBegin(GL_LINE_LOOP);
            glVertex3f((float)center.X, (float)(center.Y - cd), (float)center.Z);
            glVertex3f((float)(center.X + cd), (float)center.Y, (float)center.Z);
            glVertex3f((float)center.X, (float)(center.Y + cd), (float)center.Z);
            glVertex3f((float)(center.X - cd), (float)center.Y, (float)center.Z);
            glEnd();

            // 2. X Axis (Red Arrow)
            bool xHilite = (gizmoActive == GizmoPart.AxisX || gizmoHover == GizmoPart.AxisX);
            glLineWidth(xHilite ? 3.5f : 2.5f);
            if (xHilite)
                glColor4f(1.0f, 0.95f, 0.2f, 1.0f);
            else
                glColor4f(0.95f, 0.22f, 0.22f, 0.95f);

            glBegin(GL_LINES);
            glVertex3f((float)center.X, (float)center.Y, (float)center.Z);
            glVertex3f((float)(center.X + arrowLen), (float)center.Y, (float)center.Z);
            glEnd();

            glBegin(GL_TRIANGLES);
            glVertex3f((float)(center.X + arrowLen), (float)center.Y, (float)center.Z);
            glVertex3f((float)(center.X + arrowLen - headLen), (float)(center.Y - headWidth), (float)center.Z);
            glVertex3f((float)(center.X + arrowLen - headLen), (float)(center.Y + headWidth), (float)center.Z);
            glEnd();

            // 3. Y Axis (Green Arrow)
            bool yHilite = (gizmoActive == GizmoPart.AxisY || gizmoHover == GizmoPart.AxisY);
            glLineWidth(yHilite ? 3.5f : 2.5f);
            if (yHilite)
                glColor4f(1.0f, 0.95f, 0.2f, 1.0f);
            else
                glColor4f(0.2f, 0.92f, 0.25f, 0.95f);

            glBegin(GL_LINES);
            glVertex3f((float)center.X, (float)center.Y, (float)center.Z);
            glVertex3f((float)center.X, (float)(center.Y + arrowLen), (float)center.Z);
            glEnd();

            glBegin(GL_TRIANGLES);
            glVertex3f((float)center.X, (float)(center.Y + arrowLen), (float)center.Z);
            glVertex3f((float)(center.X - headWidth), (float)(center.Y + arrowLen - headLen), (float)center.Z);
            glVertex3f((float)(center.X + headWidth), (float)(center.Y + arrowLen - headLen), (float)center.Z);
            glEnd();

            // 4. Rotation Ring (Cyan / Gold)
            bool rotHilite = (gizmoActive == GizmoPart.Rotation || gizmoHover == GizmoPart.Rotation);
            glLineWidth(rotHilite ? 3.0f : 2.0f);
            if (rotHilite)
                glColor4f(1.0f, 0.95f, 0.2f, 1.0f);
            else
                glColor4f(0.25f, 0.75f, 1.0f, 0.85f);

            const int Segments = 48;
            glBegin(GL_LINE_LOOP);
            for (int i = 0; i < Segments; i++)
            {
                double ang = i * 2.0 * Math.PI / Segments;
                float rx = (float)(center.X + Math.Cos(ang) * ringRadius);
                float ry = (float)(center.Y + Math.Sin(ang) * ringRadius);
                glVertex3f(rx, ry, (float)center.Z);
            }
            glEnd();

            // 12 facing step ticks (every 30 degrees)
            glLineWidth(1.5f);
            glColor4f(0.8f, 0.85f, 1.0f, 0.6f);
            glBegin(GL_LINES);
            for (int i = 0; i < 12; i++)
            {
                double ang = i * Math.PI / 6.0;
                double r1 = ringRadius * 0.86;
                double r2 = ringRadius * 1.0;
                glVertex3f((float)(center.X + Math.Cos(ang) * r1), (float)(center.Y + Math.Sin(ang) * r1), (float)center.Z);
                glVertex3f((float)(center.X + Math.Cos(ang) * r2), (float)(center.Y + Math.Sin(ang) * r2), (float)center.Z);
            }
            glEnd();

            // Current facing pointer from ring
            double faceAng = facing * Math.PI / 6.0;
            float fx1 = (float)(center.X + Math.Cos(faceAng) * ringRadius * 0.70);
            float fy1 = (float)(center.Y + Math.Sin(faceAng) * ringRadius * 0.70);
            float fx2 = (float)(center.X + Math.Cos(faceAng) * ringRadius * 1.12);
            float fy2 = (float)(center.Y + Math.Sin(faceAng) * ringRadius * 1.12);

            glLineWidth(3.0f);
            glColor4f(1.0f, 0.85f, 0.1f, 1.0f);
            glBegin(GL_LINES);
            glVertex3f(fx1, fy1, (float)center.Z);
            glVertex3f(fx2, fy2, (float)center.Z);
            glEnd();

            double perpX = -Math.Sin(faceAng) * (size * 0.06);
            double perpY = Math.Cos(faceAng) * (size * 0.06);
            glBegin(GL_TRIANGLES);
            glVertex3f(fx2, fy2, (float)center.Z);
            glVertex3f((float)(fx2 - Math.Cos(faceAng) * (size * 0.10) + perpX), (float)(fy2 - Math.Sin(faceAng) * (size * 0.10) + perpY), (float)center.Z);
            glVertex3f((float)(fx2 - Math.Cos(faceAng) * (size * 0.10) - perpX), (float)(fy2 - Math.Sin(faceAng) * (size * 0.10) - perpY), (float)center.Z);
            glEnd();

            glLineWidth(1.0f);
            glEnable(GL_DEPTH_TEST);
        }

        private GizmoPart HitTestGizmo(int mouseX, int mouseY)
        {
            if (!IsGizmoEligible) return GizmoPart.None;

            Point3D center = GetGizmoWorldCenter();
            double size = GetGizmoScale(center);

            if (!GlPickRay.TryProject(center, ClientSize.Width, ClientSize.Height,
                    CenterX, CenterY, Heading, Pitch, Distance, out PointF sCenter))
                return GizmoPart.None;

            // 1. Center Handle (< 14px)
            float dxC = mouseX - sCenter.X;
            float dyC = mouseY - sCenter.Y;
            if (Math.Sqrt(dxC * dxC + dyC * dyC) <= 13.0)
                return GizmoPart.Center;

            // 2. X Axis
            Point3D xTip = new Point3D(center.X + size * 0.95, center.Y, center.Z);
            if (GlPickRay.TryProject(xTip, ClientSize.Width, ClientSize.Height,
                    CenterX, CenterY, Heading, Pitch, Distance, out PointF sTipX))
            {
                if (DistanceToSegment(new PointF(mouseX, mouseY), sCenter, sTipX) <= 9.0f)
                    return GizmoPart.AxisX;
            }

            // 3. Y Axis
            Point3D yTip = new Point3D(center.X, center.Y + size * 0.95, center.Z);
            if (GlPickRay.TryProject(yTip, ClientSize.Width, ClientSize.Height,
                    CenterX, CenterY, Heading, Pitch, Distance, out PointF sTipY))
            {
                if (DistanceToSegment(new PointF(mouseX, mouseY), sCenter, sTipY) <= 9.0f)
                    return GizmoPart.AxisY;
            }

            // 4. Rotation Ring
            double ringRadius = size * 0.85;
            const int Segs = 24;
            PointF prevPt = default;
            bool prevValid = false;
            for (int i = 0; i <= Segs; i++)
            {
                double ang = (i % Segs) * 2.0 * Math.PI / Segs;
                Point3D rPt = new Point3D(center.X + Math.Cos(ang) * ringRadius, center.Y + Math.Sin(ang) * ringRadius, center.Z);
                if (GlPickRay.TryProject(rPt, ClientSize.Width, ClientSize.Height,
                        CenterX, CenterY, Heading, Pitch, Distance, out PointF sPt))
                {
                    if (prevValid)
                    {
                        if (DistanceToSegment(new PointF(mouseX, mouseY), prevPt, sPt) <= 9.0f)
                            return GizmoPart.Rotation;
                    }
                    prevPt = sPt;
                    prevValid = true;
                }
                else
                {
                    prevValid = false;
                }
            }

            return GizmoPart.None;
        }

        private static float DistanceToSegment(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-4f)
            {
                float px = p.X - a.X;
                float py = p.Y - a.Y;
                return (float)Math.Sqrt(px * px + py * py);
            }
            float t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            float projX = a.X + t * dx;
            float projY = a.Y + t * dy;
            float distSq = (p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY);
            return (float)Math.Sqrt(distSq);
        }

        private bool RayCastGizmoPlane(int mouseX, int mouseY, double planeZ, out Point3D hit)
        {
            hit = default;
            if (!GlPickRay.TryBuildRay(mouseX, mouseY, ClientSize.Width, ClientSize.Height,
                    CenterX, CenterY, Heading, Pitch, Distance, out Point3D origin, out Vector3D dir))
                return false;

            if (Math.Abs(dir.Z) < 1e-5) return false;

            double t = (planeZ - origin.Z) / dir.Z;
            if (t <= 0) return false;

            hit = origin + dir * t;
            return true;
        }

        private void BeginGizmoDrag(GizmoPart part, int mouseX, int mouseY)
        {
            if (SelectedIndex < 0 || SelectedIndex >= map.Objects.Count) return;
            var p = map.Objects[SelectedIndex];

            gizmoActive = part;
            gizmoStartMouse = new Point(mouseX, mouseY);
            gizmoStartObjX = p.X;
            gizmoStartObjY = p.Y;
            gizmoStartFacing = p.Setting6;
            gizmoCurrentX = p.X;
            gizmoCurrentY = p.Y;
            gizmoCurrentFacing = p.Setting6;
            gizmoMoved = false;

            var selectedMeshes = new HashSet<Model3D>();
            foreach (var pair in sceneryIds)
            {
                if (pair.Value == SelectedIndex)
                    selectedMeshes.Add(pair.Key);
            }

            if (selectedMeshes.Count > 0)
            {
                gizmoDragList = BuildObjectList(SelectedIndex);
                BuildSceneryList(selectedMeshes);
            }
        }

        private void UpdateGizmoDrag(int mouseX, int mouseY)
        {
            if (gizmoActive == GizmoPart.None || SelectedIndex < 0 || SelectedIndex >= map.Objects.Count)
                return;

            Point3D center = GetGizmoWorldCenter();

            if (!RayCastGizmoPlane(mouseX, mouseY, center.Z, out Point3D hit))
                return;

            gizmoMoved = true;

            if (gizmoActive == GizmoPart.AxisX)
            {
                int nx = MapWorld.SpawnGridX(hit.X);
                nx = Math.Max(0, Math.Min(map.Width - 1, nx));
                gizmoCurrentX = nx;
                gizmoCurrentY = gizmoStartObjY;
            }
            else if (gizmoActive == GizmoPart.AxisY)
            {
                int ny = MapWorld.SpawnGridY(hit.Y);
                ny = Math.Max(0, Math.Min(map.Height - 1, ny));
                gizmoCurrentX = gizmoStartObjX;
                gizmoCurrentY = ny;
            }
            else if (gizmoActive == GizmoPart.Center)
            {
                int nx = MapWorld.SpawnGridX(hit.X);
                int ny = MapWorld.SpawnGridY(hit.Y);
                nx = Math.Max(0, Math.Min(map.Width - 1, nx));
                ny = Math.Max(0, Math.Min(map.Height - 1, ny));
                gizmoCurrentX = nx;
                gizmoCurrentY = ny;
            }
            else if (gizmoActive == GizmoPart.Rotation)
            {
                double origWx = MapWorld.SpawnX(gizmoStartObjX);
                double origWy = MapWorld.SpawnY(gizmoStartObjY);
                double dx = hit.X - origWx;
                double dy = hit.Y - origWy;
                double ang = Math.Atan2(dy, dx);
                if (ang < 0) ang += 2 * Math.PI;

                int step = (int)Math.Round(ang / (Math.PI / 6.0)) % 12;
                if (step < 0) step += 12;
                gizmoCurrentFacing = (byte)step;
            }
        }

        private void EndGizmoDrag(bool commit)
        {
            if (gizmoActive == GizmoPart.None) return;

            GizmoPart finishedPart = gizmoActive;
            int targetX = gizmoCurrentX;
            int targetY = gizmoCurrentY;
            byte targetFacing = gizmoCurrentFacing;
            bool wasMoved = gizmoMoved;

            gizmoActive = GizmoPart.None;
            gizmoMoved = false;

            if (gizmoDragList != 0)
            {
                glDeleteLists(gizmoDragList, 1);
                gizmoDragList = 0;
            }

            BuildSceneryList();

            if (commit && wasMoved && SelectedIndex >= 0 && SelectedIndex < map.Objects.Count)
            {
                if (finishedPart == GizmoPart.AxisX || finishedPart == GizmoPart.AxisY || finishedPart == GizmoPart.Center)
                {
                    if (targetX != gizmoStartObjX || targetY != gizmoStartObjY)
                    {
                        MoveSelectedTo(targetX, targetY);
                    }
                }
                else if (finishedPart == GizmoPart.Rotation)
                {
                    if (targetFacing != gizmoStartFacing)
                    {
                        if (CommitRotation != null)
                            SyncPlacements(CommitRotation(SelectedIndex, targetFacing));
                        else
                            map.Objects[SelectedIndex].Setting6 = targetFacing;
                    }
                }
            }

            Changed();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void DrawDraggedGizmoObject()
        {
            if (gizmoActive == GizmoPart.None || gizmoDragList == 0 || SelectedIndex < 0 || SelectedIndex >= map.Objects.Count)
                return;

            var p = map.Objects[SelectedIndex];

            if (gizmoActive == GizmoPart.Rotation)
            {
                double cx = MapWorld.SpawnX(gizmoStartObjX);
                double cy = MapWorld.SpawnY(gizmoStartObjY);
                double cz = ObjectZ(p, gizmoStartObjX, gizmoStartObjY);
                double deltaDeg = (gizmoCurrentFacing - gizmoStartFacing) * 30.0;

                glPushMatrix();
                glTranslatef((float)cx, (float)cy, (float)cz);
                glRotatef((float)deltaDeg, 0, 0, 1);
                glTranslatef((float)-cx, (float)-cy, (float)-cz);
                glCallList(gizmoDragList);
                glPopMatrix();
            }
            else
            {
                double startWx = MapWorld.SpawnX(gizmoStartObjX);
                double startWy = MapWorld.SpawnY(gizmoStartObjY);
                double startWz = ObjectZ(p, gizmoStartObjX, gizmoStartObjY);

                double curWx = MapWorld.SpawnX(gizmoCurrentX);
                double curWy = MapWorld.SpawnY(gizmoCurrentY);
                double curWz = ObjectZ(p, gizmoCurrentX, gizmoCurrentY);

                glPushMatrix();
                glTranslatef((float)(curWx - startWx), (float)(curWy - startWy), (float)(curWz - startWz));
                glCallList(gizmoDragList);
                glPopMatrix();
            }
        }
    }
}
