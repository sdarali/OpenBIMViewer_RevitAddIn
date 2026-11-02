using Autodesk.Revit.DB;
using OpenBimViewer.Collab;

namespace OpenBimViewer.Revit.Services
{
    /// <summary>
    /// BCF coordinates (the IFC file's: metres, Z up) ↔ Revit's internal coordinates (feet). Which
    /// IFC origin applies depends on how the model was exported to IFC (<see cref="CoordinateBase"/>).
    /// </summary>
    internal sealed class IfcFrame
    {
        private const double MetresPerFoot = 0.3048;

        private readonly Transform _internalToIfc;
        private readonly Transform _ifcToInternal;

        private IfcFrame(Transform internalToIfc)
        {
            _internalToIfc = internalToIfc;
            _ifcToInternal = internalToIfc.Inverse;
        }

        public static IfcFrame For(Document doc, CoordinateBase coordinateBase)
        {
            switch (coordinateBase)
            {
                case CoordinateBase.Internal:
                    return new IfcFrame(Transform.Identity);
                case CoordinateBase.ProjectBasePoint:
                    return new IfcFrame(Transform.CreateTranslation(-BasePoint.GetProjectBasePoint(doc).Position));
                default:
                    return new IfcFrame(InternalToShared(doc));
            }
        }

        /// <summary>
        /// Internal → shared coordinates. The project base point knows both its internal and its
        /// shared position, which settles which way the project location's transform points.
        /// </summary>
        private static Transform InternalToShared(Document doc)
        {
            var total = doc.ActiveProjectLocation.GetTotalTransform();
            var basePoint = BasePoint.GetProjectBasePoint(doc);
            var inverse = total.Inverse;
            return inverse.OfPoint(basePoint.Position).DistanceTo(basePoint.SharedPosition)
                   <= total.OfPoint(basePoint.Position).DistanceTo(basePoint.SharedPosition)
                ? inverse
                : total;
        }

        public XYZ ToRevitPoint(Vec3 ifc) => _ifcToInternal.OfPoint(new XYZ(ifc.X, ifc.Y, ifc.Z) / MetresPerFoot);

        public XYZ ToRevitDirection(Vec3 ifc) => _ifcToInternal.OfVector(new XYZ(ifc.X, ifc.Y, ifc.Z)).Normalize();

        public Vec3 ToIfcPoint(XYZ revit)
        {
            var p = _internalToIfc.OfPoint(revit) * MetresPerFoot;
            return new Vec3(p.X, p.Y, p.Z);
        }

        public Vec3 ToIfcDirection(XYZ revit)
        {
            var v = _internalToIfc.OfVector(revit).Normalize();
            return new Vec3(v.X, v.Y, v.Z);
        }
    }
}
