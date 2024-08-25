﻿using Gum.Converters;
using Gum.DataTypes;
using Gum.DataTypes.Variables;
using Gum.Managers;
using Gum.RenderingLibrary;
using GumDataTypes.Variables;

using RenderingLibrary;
using RenderingLibrary.Graphics;
using RenderingLibrary.Math;


using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using ToolsUtilitiesStandard.Helpers;
using MathHelper = ToolsUtilitiesStandard.Helpers.MathHelper;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using Matrix = System.Numerics.Matrix4x4;
using GumRuntime;
#if UWP
using System.Reflection;
#endif

namespace Gum.Wireframe
{
    #region Enums

    public enum MissingFileBehavior
    {
        ConsumeSilently,
        ThrowException
    }

    #endregion

    /// <summary>
    /// The base object for all Gum runtime objects. It contains functionality for
    /// setting variables, states, and performing layout. The GraphicalUiElement can
    /// wrap an underlying rendering object.
    /// </summary>
    public partial class GraphicalUiElement : IRenderableIpso, IVisible, INotifyPropertyChanged
    {
        #region Enums/Internal Classes

        enum ChildType
        {
            Absolute = 1,
            Relative = 1 << 1,
            BothAbsoluteAndRelative = Absolute | Relative,
            StackedWrapped = 1 << 2,
            All = Absolute | Relative | StackedWrapped
        }

        class DirtyState
        {
            public ParentUpdateType ParentUpdateType;
            public int ChildrenUpdateDepth;
            public XOrY? XOrY;
        }

        public enum ParentUpdateType
        {
            None = 0,
            IfParentStacks = 1,
            IfParentWidthHeightDependOnChildren = 2,
            All = 4

        }

        #endregion

        #region Fields

        public static float GlobalFontScale = 1;

        private DirtyState currentDirtyState;
        bool isFontDirty = false;
        public bool IsFontDirty
        {
            get => isFontDirty;
            set => isFontDirty = value;
        }

        public static int UpdateLayoutCallCount;
        public static int ChildrenUpdatingParentLayoutCalls;

        // This used to be true until Jan 26, 2024, but it's
        // confusing for new users. Let's keep this off and document
        // how to use it (eventually).
        public static bool ShowLineRectangles = false;

        // to save on casting:
        protected IRenderableIpso mContainedObjectAsIpso;
        protected IVisible mContainedObjectAsIVisible;

        GraphicalUiElement mWhatContainsThis;

        /// <summary>
        /// A flat list of all GraphicalUiElements contained by this element. For example, if this GraphicalUiElement
        /// is a Screen, this list is all GraphicalUielements for every instance contained regardless of hierarchy.
        /// </summary>
        List<GraphicalUiElement> mWhatThisContains = new List<GraphicalUiElement>();

        protected List<GraphicalUiElement> WhatThisContains => mWhatThisContains;

        Dictionary<string, string> mExposedVariables = new Dictionary<string, string>();

        GeneralUnitType mXUnits;
        GeneralUnitType mYUnits;
        HorizontalAlignment mXOrigin;
        VerticalAlignment mYOrigin;
        DimensionUnitType mWidthUnit;
        DimensionUnitType mHeightUnit;

        protected ISystemManagers mManagers;

        int mTextureTop;
        int mTextureLeft;
        int mTextureWidth;
        int mTextureHeight;
        bool mWrap;

        bool mWrapsChildren = false;

        float mTextureWidthScale;
        float mTextureHeightScale;

        TextureAddress mTextureAddress;

        float mX;
        float mY;
        protected float mWidth;
        protected float mHeight;
        float mRotation;

        IRenderableIpso mParent;

        protected bool mIsLayoutSuspended = false;
        public bool IsLayoutSuspended => mIsLayoutSuspended;

        // We need ThreadStatic in case screens are being loaded
        // in the background - we don't want to interrupt the foreground
        // layout behavior.
        [ThreadStatic]
        public static bool IsAllLayoutSuspended = false;

        Dictionary<string, Gum.DataTypes.Variables.StateSave> mStates =
            new Dictionary<string, DataTypes.Variables.StateSave>();

        Dictionary<string, Gum.DataTypes.Variables.StateSaveCategory> mCategories =
            new Dictionary<string, Gum.DataTypes.Variables.StateSaveCategory>();

        // This needs to be made public so that individual Forms objects can be customized:
        public Dictionary<string, Gum.DataTypes.Variables.StateSaveCategory> Categories => mCategories;

        // the row or column index when anobject is sorted.
        // This is used by the stacking logic to properly sort objects
        public int StackedRowOrColumnIndex { get; set; } = -1;

        // null by default, non-null if an object uses
        // stacked layout for its children.
        public List<float> StackedRowOrColumnDimensions { get; private set; }
        #endregion

        #region Properties

        ColorOperation IRenderableIpso.ColorOperation => mContainedObjectAsIpso.ColorOperation;

        public static MissingFileBehavior MissingFileBehavior { get; set; } = MissingFileBehavior.ConsumeSilently;

        public ElementSave ElementSave
        {
            get;
            set;
        }

        public ISystemManagers Managers
        {
            get
            {
                return mManagers;
            }
        }

        /// <summary>
        /// Returns this instance's SystemManagers, or climbs up the parent/child relationship
        /// until a non-null SystemsManager is found. Otherwise, returns null.
        /// </summary>
        public ISystemManagers EffectiveManagers
        {
            get
            {
                if (mManagers != null)
                {
                    return mManagers;
                }
                else
                {
                    return this.ElementGueContainingThis?.EffectiveManagers ??
                        this.EffectiveParentGue?.EffectiveManagers;
                }
            }
        }

        public bool Visible
        {
            get
            {
                if (mContainedObjectAsIVisible != null)
                {
                    return mContainedObjectAsIVisible.Visible;
                }
                else
                {
                    return false;
                }
            }
            set
            {
                // If this is a Screen, then it doesn't have a contained IVisible:
                if (mContainedObjectAsIVisible != null && value != mContainedObjectAsIVisible.Visible)
                {
                    mContainedObjectAsIVisible.Visible = value;

                    var absoluteVisible = ((IVisible)this).AbsoluteVisible;
                    // See if this has a parent that stacks children. If so, update its layout:

                    var didUpdate = false;
                    if(absoluteVisible)
                    {
                        if(!mIsLayoutSuspended && !GraphicalUiElement.IsAllLayoutSuspended)
                        {
                            // resume layout:
                            // This does need to be recursive because contained objects may have been 
                            // updated while the parent was invisible, becoming dirty, and waiting for
                            // the resume
                            didUpdate = ResumeLayoutUpdateIfDirtyRecursive();

                            //if (isFontDirty)
                            //{
                            //    if (!IsAllLayoutSuspended)
                            //    {
                            //        this.UpdateToFontValues();
                            //        isFontDirty = false;
                            //    }
                            //}
                            //if (currentDirtyState != null)
                            //{
                            //    UpdateLayout(currentDirtyState.ParentUpdateType,
                            //        currentDirtyState.ChildrenUpdateDepth,
                            //        currentDirtyState.XOrY);
                            //}

                            if(this.WidthUnits == DimensionUnitType.Ratio || this.HeightUnits == DimensionUnitType.Ratio)
                            {
                                // If this is a width or height ratio and we're made visible, then the parent needs to update if it stacks:
                                this.UpdateLayout(ParentUpdateType.IfParentStacks,
                                    // If something is made visible, that shouldn't update the children, right?
                                    //int.MaxValue/2, 
                                    0,
                                    null);
                                didUpdate = true;
                            }
                        }
                    }
                    
                    if(!didUpdate)
                    {
                        // This will make this dirty:
                        this.UpdateLayout(ParentUpdateType.IfParentStacks | ParentUpdateType.IfParentWidthHeightDependOnChildren, 
                            // If something is made visible, that shouldn't update the children, right?
                            //int.MaxValue/2, 
                            0,
                            null);
                    }

                    if(!absoluteVisible && GetIfParentStacks())
                    {
                        // This updates the parent right away:
                        (Parent as GraphicalUiElement)?.UpdateLayout(ParentUpdateType.IfParentStacks, int.MaxValue / 2, null);

                    }
                }
            }
        }

        /// <summary>
        /// The X "world units" that the entire gum rendering system uses. This is essentially the "top level" container's width.
        /// For a game which renders at 1:1, this will match the game's resolution. 
        /// </summary>
        public static float CanvasWidth
        {
            get;
            set;
        }

        /// <summary>
        /// The Y "world units" that the entire gum rendering system uses. This is essentially the "top level" container's height.
        /// For a game which renders at 1:1, this will match the game's resolution. 
        /// </summary>
        public static float CanvasHeight
        {
            get;
            set;
        }

        #region IPSO properties

        /// <summary>
        /// The X position of this object as an IPositionedSizedObject. This does not consider origins
        /// so it will use the default origin, which is top-left for most types.
        /// </summary>
        float IPositionedSizedObject.X
        {
            get
            {
                // this used to throw an exception, but 
                // the screen is an IPSO which may be considered
                // the effective parent of an element.
                if (mContainedObjectAsIpso == null)
                {
                    return 0;
                }
                else
                {
                    return mContainedObjectAsIpso.X;
                }
            }
            set
            {
                throw new InvalidOperationException("This is a GraphicalUiElement. You must cast the instance to GraphicalUiElement to set its X so that its XUnits apply.");
            }
        }

        /// <summary>
        /// The Y position of this object as an IPositionedSizedObject. This does not consider origins
        /// so it will use the default origin, which is top-left for most types.
        /// </summary>
        float IPositionedSizedObject.Y
        {
            get
            {
                if (mContainedObjectAsIpso == null)
                {
                    return 0;
                }
                else
                {
                    return mContainedObjectAsIpso.Y;
                }
            }
            set
            {
                throw new InvalidOperationException("This is a GraphicalUiElement. You must cast the instance to GraphicalUiElement to set its Y so that its YUnits apply.");
            }
        }

        float IPositionedSizedObject.Rotation
        {
            get => mContainedObjectAsIpso?.Rotation ?? 0;
            set
            {
                throw new InvalidOperationException(
                    "This is a GraphicalUiElement. You must cast the instance to GraphicalUiElement to set its Rotation so that its layout apply.");

            }
        }

        float IPositionedSizedObject.Width
        {
            get
            {
                if (mContainedObjectAsIpso == null)
                {
                    return GraphicalUiElement.CanvasWidth;
                }
                else
                {
                    return mContainedObjectAsIpso.Width;
                }
            }
            set
            {
                mContainedObjectAsIpso.Width = value;
            }
        }

        float IPositionedSizedObject.Height
        {
            get
            {
                if (mContainedObjectAsIpso == null)
                {
                    return GraphicalUiElement.CanvasHeight;
                }
                else
                {
                    return mContainedObjectAsIpso.Height;
                }
            }
            set
            {
                mContainedObjectAsIpso.Height = value;
            }
        }

        /// <summary>
        /// Returns the absolute width of the GraphicalUiElement in pixels (as opposed to using its WidthUnits)
        /// </summary>
        /// <returns>The absolute width in pixels.</returns>
        public float GetAbsoluteWidth() => ((IPositionedSizedObject)this).Width;

        /// <summary>
        /// Returns the absolute height of the GraphicalUiElement in pixels (as opposed to using its HeightUnits)
        /// </summary>
        /// <returns>The absolute height in pixels.</returns>
        public float GetAbsoluteHeight() => ((IPositionedSizedObject)this).Height;

        void IRenderableIpso.SetParentDirect(IRenderableIpso parent)
        {
            mContainedObjectAsIpso.SetParentDirect(parent);
        }

        #endregion

        public float Z
        {
            get
            {
                if (mContainedObjectAsIpso == null)
                {
                    return 0;
                }
                else
                {
                    return mContainedObjectAsIpso.Z;
                }
            }
            set
            {
                mContainedObjectAsIpso.Z = value;
            }
        }

        #region IRenderable properties


        BlendState IRenderable.BlendState
        {
            get
            {
#if DEBUG
                if (mContainedObjectAsIpso == null)
                {
                    throw new NullReferenceException("This GraphicalUiElemente has not had its visual set, so it does not have a blend operation. This can happen if a GraphicalUiElement was added as a child without its contained renderable having been set.");
                }
#endif
                return mContainedObjectAsIpso.BlendState;
            }
        }


        bool IRenderable.Wrap
        {
            get { return mContainedObjectAsIpso.Wrap; }
        }

        public virtual void Render(ISystemManagers managers)
        {
            mContainedObjectAsIpso.Render(managers);
        }


        Layer mLayer;

        #endregion

        public GeneralUnitType XUnits
        {
            get => mXUnits;
            set
            {
                if (value != mXUnits)
                {
                    mXUnits = value;
                    UpdateLayout();
                }
            }
        }

        public GeneralUnitType YUnits
        {
            get { return mYUnits; }
            set
            {
                if (mYUnits != value)
                {
                    mYUnits = value;
                    UpdateLayout();
                }
            }
        }

        public HorizontalAlignment XOrigin
        {
            get { return mXOrigin; }
            set
            {
                if (mXOrigin != value)
                {
                    mXOrigin = value; UpdateLayout();
                }
            }
        }

        public VerticalAlignment YOrigin
        {
            get { return mYOrigin; }
            set
            {
                if (mYOrigin != value)
                {
                    mYOrigin = value; UpdateLayout();
                }
            }
        }

        public DimensionUnitType WidthUnits
        {
            get { return mWidthUnit; }
            set
            {
                if (mWidthUnit != value)
                {
                    mWidthUnit = value; UpdateLayout();
                }
            }
        }

        public DimensionUnitType HeightUnits
        {
            get { return mHeightUnit; }
            set
            {
                if (mHeightUnit != value)
                {
                    mHeightUnit = value; 

                    if(mContainedObjectAsIpso is IText)
                    {
                        RefreshTextOverflowVerticalMode();
                    }

                    UpdateLayout();
                }
            }
        }


        bool ignoredByParentSize;
        public bool IgnoredByParentSize
        {
            get => ignoredByParentSize;
            set
            {
                if(ignoredByParentSize != value)
                {
                    ignoredByParentSize = value;
                    // todo - could be smarter here?
                    UpdateLayout();
                }
            }
        }

        ChildrenLayout childrenLayout;
        public ChildrenLayout ChildrenLayout
        {
            get => childrenLayout;
            set
            {
                if(value != childrenLayout)
                {
                    childrenLayout = value; UpdateLayout();
                }
            }
        }

        int autoGridHorizontalCells = 4;
        public int AutoGridHorizontalCells
        {
            get => autoGridHorizontalCells;
            set
            {
                if(autoGridHorizontalCells != value)
                {
                    autoGridHorizontalCells = value; UpdateLayout();
                }
            }
        }

        int autoGridVerticalCells = 4;
        public int AutoGridVerticalCells
        {
            get => autoGridVerticalCells;
            set
            {
                if (autoGridVerticalCells != value)
                {
                    autoGridVerticalCells = value; UpdateLayout();
                }
            }
        }

        TextOverflowVerticalMode textOverflowVerticalMode;
        // we have to store this locally because we are going to effectively assign the overflow mode based on the height units and this value
        public TextOverflowVerticalMode TextOverflowVerticalMode
        {
            get => textOverflowVerticalMode;
            set
            {
                if(textOverflowVerticalMode != value)
                {
                    if(this.RenderableComponent is IText text)
                    {
                        text.TextOverflowVerticalMode = value;
                    }
                    textOverflowVerticalMode = value;
                }
            }
        }

        float stackSpacing;
        /// <summary>
        /// The number of pixels spacing between each child if this is has a ChildrenLayout of 
        /// TopToBottomStack or LeftToRightStack.
        /// </summary>
        public float StackSpacing
        {
            get => stackSpacing;
            set
            {
                if(stackSpacing != value)
                {
                    stackSpacing = value; 
                    if(ChildrenLayout != ChildrenLayout.Regular)
                    {
                        UpdateLayout();
                    }
                }
            }
        }

        bool useFixedStackChildrenSize;
        public bool UseFixedStackChildrenSize
        {
            get => useFixedStackChildrenSize;
            set
            {
                if(useFixedStackChildrenSize != value)
                {
                    useFixedStackChildrenSize = value;
                    if (ChildrenLayout != ChildrenLayout.Regular)
                    {
                        UpdateLayout();
                    }
                }
            }
        }

        /// <summary>
        /// Rotation in degrees. Positive value rotates counterclockwise.
        /// </summary>
        public float Rotation
        {
            get
            {
                return mRotation;
            }
            set
            {
#if DEBUG
                if (float.IsNaN(value) || float.IsPositiveInfinity(value) || float.IsNegativeInfinity(value))
                {
                    throw new Exception($"Invalid Rotaiton value set: {value}");
                }
#endif
                if (mRotation != value)
                {
                    mRotation = value;

                    UpdateLayout();
                }
            }
        }

        public bool FlipHorizontal
        {
            get => mContainedObjectAsIpso?.FlipHorizontal ?? false;
            set
            {
                if (mContainedObjectAsIpso != null)
                {
                    if (mContainedObjectAsIpso.FlipHorizontal != value)
                    {
                        mContainedObjectAsIpso.FlipHorizontal = value;
                        UpdateLayout();
                    }
                }
            }
        }

        public float X
        {
            get
            {
                return mX;
            }
            set
            {
                if (mX != value && mContainedObjectAsIpso != null)
                {
#if DEBUG
                    if (float.IsNaN(value))
                    {
                        throw new ArgumentException("Not a Number (NAN) not allowed");
                    }
#endif
                    mX = value;

                    var parentGue = Parent as GraphicalUiElement;
                    // special case:
                    if (Parent as GraphicalUiElement == null && XUnits == GeneralUnitType.PixelsFromSmall && XOrigin == HorizontalAlignment.Left)
                    {
                        this.mContainedObjectAsIpso.X = mX;
                    }
                    else
                    {
                        var refreshParent = IgnoredByParentSize == false;
                        UpdateLayout(refreshParent, 0);
                    }
                }
            }
        }

        public float Y
        {
            get
            {
                return mY;
            }
            set
            {
                if (mY != value && mContainedObjectAsIpso != null)
                {
#if DEBUG
                    if (float.IsNaN(value))
                    {
                        throw new ArgumentException("Not a Number (NAN) not allowed");
                    }
#endif
                    mY = value;


                    if (Parent as GraphicalUiElement == null && YUnits == GeneralUnitType.PixelsFromSmall && YOrigin == VerticalAlignment.Top)
                    {
                        this.mContainedObjectAsIpso.Y = mY;
                    }
                    else
                    {
                        var refreshParent = IgnoredByParentSize == false;
                        UpdateLayout(refreshParent, 0);
                    }
                }
            }
        }

        public float Width
        {
            get { return mWidth; }
            set
            {
#if DEBUG
                if (float.IsPositiveInfinity(value) ||
                    float.IsNegativeInfinity(value) ||
                    float.IsNaN(value))
                {
                    throw new ArgumentException();
                }
#endif
                if (mWidth != value)
                {
                    mWidth = value; UpdateLayout();
                }
            }
        }

        public float Height
        {
            get { return mHeight; }
            set
            {
                if (mHeight != value)
                {
#if DEBUG
                    if (float.IsPositiveInfinity(value) ||
                        float.IsNegativeInfinity(value) ||
                        float.IsNaN(value))
                    {
                        throw new ArgumentException();
                    }
#endif
                    mHeight = value; UpdateLayout();
                }
            }
        }

        public IRenderableIpso Parent
        {
            get { return mParent; }
            set
            {
#if DEBUG
                if (value == this)
                {
                    throw new InvalidOperationException("Cannot attach an object to itself");
                }
#endif
                if (mParent != value)
                {
                    if (mParent != null && mParent.Children != null)
                    {
                        mParent.Children.Remove(this);
                        (mParent as GraphicalUiElement)?.UpdateLayout();
                    }
                    mParent = value;

                    // In case the object was added explicitly 
                    if (mParent?.Children != null && mParent.Children.Contains(this) == false)
                    {
                        mParent.Children.Add(this);
                    }
                    
                    // If layout is suppressed, the parent may not get set
                    // and it's possible to have a floating visible=true object
                    // that gets rendered without a parent:
                    mContainedObjectAsIpso?.SetParentDirect(value);

                    UpdateLayout();

                    ParentChanged?.Invoke(this, null);
                }
            }
        }

        // Made obsolete November 4, 2017
        [Obsolete("Use ElementGueContainingThis instead - it more clearly indicates the relationship, " +
            "as the ParentGue may not actually be the parent. If the effective parent is desired, use EffectiveParentGue")]
        public GraphicalUiElement ParentGue
        {
            get { return ElementGueContainingThis; }
            set { ElementGueContainingThis = value; }
        }

        /// <summary>
        /// The ScreenSave or Component which contains this instance.
        /// </summary>
        public GraphicalUiElement ElementGueContainingThis
        {
            get
            {
                return mWhatContainsThis;
            }
            set
            {
                if (mWhatContainsThis != value)
                {
                    if (mWhatContainsThis != null)
                    {
                        mWhatContainsThis.mWhatThisContains.Remove(this); ;
                    }

                    mWhatContainsThis = value;

                    if (mWhatContainsThis != null)
                    {
                        mWhatContainsThis.mWhatThisContains.Add(this);
                    }
                }
            }
        }

        public GraphicalUiElement EffectiveParentGue
        {
            get
            {
                if (Parent != null && Parent is GraphicalUiElement)
                {
                    return Parent as GraphicalUiElement;
                }
                else
                {
                    return ElementGueContainingThis;
                }
            }
        }

        public IRenderable RenderableComponent
        {
            get
            {
                if (mContainedObjectAsIpso is GraphicalUiElement)
                {
                    return ((GraphicalUiElement)mContainedObjectAsIpso).RenderableComponent;
                }
                else
                {
                    return mContainedObjectAsIpso;
                }

            }
        }

        /// <summary>
        /// Returns an enumerable for all GraphicalUiElements that this contains.
        /// </summary>
        /// <remarks>
        /// Since this is an interface using ContainedElements in a foreach allocates memory
        /// and this can actually be significant in a game that updates its UI frequently.
        /// </remarks>
        public IList<GraphicalUiElement> ContainedElements
        {
            get
            {
                return mWhatThisContains;
            }
        }

        string name;
        public string Name
        {
            get => name;
            set
            {
                if (mContainedObjectAsIpso != null)
                {
                    mContainedObjectAsIpso.Name = value;
                }
                name = value;
            }
        }

        /// <summary>
        /// Returns the direct hierarchical children of this. Note that this does not return all objects contained in the element, only direct children. 
        /// </summary>

        public ObservableCollection<IRenderableIpso> Children
        {
            get
            {
                return mContainedObjectAsIpso?.Children;
            }
        }

        object mTagIfNoContainedObject;
        public object Tag
        {
            get
            {
                if (mContainedObjectAsIpso != null)
                {
                    return mContainedObjectAsIpso.Tag;
                }
                else
                {
                    return mTagIfNoContainedObject;
                }
            }
            set
            {
                if (mContainedObjectAsIpso != null)
                {
                    mContainedObjectAsIpso.Tag = value;
                }
                else
                {
                    mTagIfNoContainedObject = value;
                }
            }
        }

        public IPositionedSizedObject Component { get { return mContainedObjectAsIpso; } }

        /// <summary>
        /// Returns the absolute (screen space) X of the origin of the GraphicalUiElement. Note that
        /// this considers the XOrigin, and will apply rotation.
        /// </summary>
        public float AbsoluteX
        {
            get
            {
                float toReturn = this.GetAbsoluteX();

                var originOffset = Vector2.Zero;

                switch (XOrigin)
                {
                    case HorizontalAlignment.Center:
                        originOffset.X = ((IPositionedSizedObject)this).Width / 2;

                        break;
                    case HorizontalAlignment.Right:
                        originOffset.X = ((IPositionedSizedObject)this).Width;
                        break;
                }

                switch (YOrigin)
                {
                    case VerticalAlignment.TextBaseline:
                        originOffset.Y = ((IPositionedSizedObject)this).Height;
                        if (mContainedObjectAsIpso is IText text)
                        {
                            originOffset.Y -= text.DescenderHeight * text.FontScale;
                        }
                        break;
                    case VerticalAlignment.Center:
                        originOffset.Y = ((IPositionedSizedObject)this).Height / 2;
                        break;
                    case VerticalAlignment.Bottom:
                        originOffset.Y = ((IPositionedSizedObject)this).Height;
                        break;
                }

                var matrix = this.GetAbsoluteRotationMatrix();
                originOffset = Vector2.Transform(originOffset, matrix);
                return toReturn + originOffset.X;
            }
        }

        /// <summary>
        /// Returns the absolute X (in screen space) of the left edge of the GraphicalUielement.
        /// </summary>
        public float AbsoluteLeft => this.GetAbsoluteX();

        /// <summary>
        /// Returns the absolute Y (screen space) of the origin of the GraphicalUiElement. Note that
        /// this considers the YOrigin, and will apply rotation
        /// </summary>
        public float AbsoluteY
        {
            get
            {
                float toReturn = this.GetAbsoluteY();

                var originOffset = Vector2.Zero;

                switch (XOrigin)
                {
                    case HorizontalAlignment.Center:
                        originOffset.X = ((IPositionedSizedObject)this).Width / 2;

                        break;
                    case HorizontalAlignment.Right:
                        originOffset.X = ((IPositionedSizedObject)this).Width;
                        break;
                }

                switch (YOrigin)
                {
                    case VerticalAlignment.TextBaseline:
                        originOffset.Y = ((IPositionedSizedObject)this).Height;
                        if (mContainedObjectAsIpso is IText text)
                        {
                            originOffset.Y -= text.DescenderHeight * text.FontScale;
                        }
                        break;
                    case VerticalAlignment.Center:
                        originOffset.Y = ((IPositionedSizedObject)this).Height / 2;
                        break;
                    case VerticalAlignment.Bottom:
                        originOffset.Y = ((IPositionedSizedObject)this).Height;
                        break;
                }
                var matrix = this.GetAbsoluteRotationMatrix();
                originOffset = Vector2.Transform(originOffset, matrix);

                return toReturn + originOffset.Y;
            }
        }

        /// <summary>
        /// Returns the absolute Y (in screen space) of the top edge of the GraphicalUiElement.
        /// </summary>
        public float AbsoluteTop => this.GetAbsoluteY();

        public IVisible ExplicitIVisibleParent
        {
            get;
            set;
        }

        /// <summary>
        /// The pixel coorinate of the top of the displayed region.
        /// </summary>
        public int TextureTop
        {
            get
            {
                return mTextureTop;
            }
            set
            {
                if (mTextureTop != value)
                {
                    mTextureTop = value;
                    // changing the texture top won't update the dimensions, just
                    // the contained graphical object. 
                    UpdateLayout(updateParent: false, updateChildren: false);

                }
            }
        }

        /// <summary>
        /// The pixel coorinate of the left of the displayed region.
        /// </summary>
        public int TextureLeft
        {
            get
            {
                return mTextureLeft;
            }
            set
            {
                if (mTextureLeft != value)
                {
                    mTextureLeft = value;
                    UpdateLayout(updateParent: false, updateChildren: false);
                }
            }
        }

        /// <summary>
        /// The pixel width of the source rectangle on the referenced texture.
        /// </summary>
        public int TextureWidth
        {
            get
            {
                return mTextureWidth;
            }
            set
            {
                if (mTextureWidth != value)
                {
                    mTextureWidth = value;
                    UpdateLayout();
                }
            }
        }

        /// <summary>
        /// The pixel height of the source rectangle on the referenced texture.
        /// </summary>
        public int TextureHeight
        {
            get
            {
                return mTextureHeight;
            }
            set
            {
                if (mTextureHeight != value)
                {
                    mTextureHeight = value;
                    UpdateLayout();
                }
            }
        }

        public float TextureWidthScale
        {
            get
            {
                return mTextureWidthScale;
            }
            set
            {
                if (mTextureWidthScale != value)
                {
                    mTextureWidthScale = value;
                    UpdateLayout();
                }
            }
        }
        public float TextureHeightScale
        {
            get
            {
                return mTextureHeightScale;
            }
            set
            {
                if (mTextureHeightScale != value)
                {
                    mTextureHeightScale = value;
                    UpdateLayout();
                }
            }
        }

        public TextureAddress TextureAddress
        {
            get
            {
                return mTextureAddress;
            }
            set
            {
                if (mTextureAddress != value)
                {
                    mTextureAddress = value;
                    UpdateLayout();
                }
            }
        }

        /// <summary>
        /// Whether the texture address should wrap.
        /// </summary>
        public bool Wrap
        {
            get
            {
                return mWrap;
            }
            set
            {
                if (mWrap != value)
                {
                    mWrap = value;
                    UpdateLayout();
                }
            }
        }

        /// <summary>
        /// Whether contained children should wrap. This only applies if ChildrenLayout is set to 
        /// ChildrenLayout.LeftToRightStack or ChildrenLayout.TopToBottomStack.
        /// </summary>
        public bool WrapsChildren
        {
            get { return mWrapsChildren; }
            set
            {
                if (mWrapsChildren != value)
                {
                    mWrapsChildren = value; UpdateLayout();
                }
            }
        }

        /// <summary>
        /// Whether the rendering of this object's children should be clipped to the bounds of this object. If false
        /// then children can render outside of the bounds of this object.
        /// </summary>
        public bool ClipsChildren
        {
            get;
            set;
        }


        #endregion

        #region Events

        // It's possible that a size change could result in a layout which 
        // results in a further size change. This recursive call of size changes
        // could happen indefinitely so we only want to do this one time.
        // This prevents the size change from happening over and over:
        bool isInSizeChange;
        /// <summary>
        /// Event raised whenever this instance's absolute size changes. This size change can occur by a direct value being
        /// set (such as Width or WidthUnits), or by an indirect value changing, such as if a Parent is resized and if
        /// this uses a WidthUnits depending on the parent.
        /// </summary>
        public event EventHandler SizeChanged;
        public event EventHandler PositionChanged;
        public event EventHandler ParentChanged;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (PropertyChanged != null)
            {
                var args = new PropertyChangedEventArgs(propertyName);
                PropertyChanged(this, args);
            }
        }

        public static Action<IText, GraphicalUiElement> UpdateFontFromProperties;
        public static Action<GraphicalUiElement> ThrowExceptionsForMissingFiles;
        public static Action<IRenderableIpso, ISystemManagers> RemoveRenderableFromManagers;
        public static Action<IRenderableIpso, ISystemManagers, Layer> AddRenderableToManagers;
        public static Action<string, GraphicalUiElement> ApplyMarkup;

        public static Action<IRenderableIpso, GraphicalUiElement, string, object> SetPropertyOnRenderable =
            // This is the default fallback to make Gum work. Specific rendering libraries can change this to provide 
            // better performance.
            SetPropertyThroughReflection;


        #endregion

        #region Constructor

        public GraphicalUiElement()
            : this(null, null)
        {
            mIsLayoutSuspended = true;
            Width = 32;
            Height = 32;
            mIsLayoutSuspended = false;
        }

        public GraphicalUiElement(IRenderable containedObject, GraphicalUiElement whatContainsThis = null)
        {
            SetContainedObject(containedObject);

            mWhatContainsThis = whatContainsThis;
            if (mWhatContainsThis != null)
            {
                mWhatContainsThis.mWhatThisContains.Add(this);

                // I don't think we want to do this. 
                if (whatContainsThis.mContainedObjectAsIpso != null)
                {
                    this.Parent = whatContainsThis;
                }
            }
        }

        public void SetContainedObject(IRenderable containedObject)
        {
            if (containedObject == this)
            {
                throw new ArgumentException("The argument containedObject cannot be 'this'");
            }


            if (mContainedObjectAsIpso != null)
            {
                mContainedObjectAsIpso.Children.CollectionChanged -= HandleCollectionChanged;
                if (string.IsNullOrEmpty(this.Name) && !string.IsNullOrEmpty(mContainedObjectAsIpso.Name))
                {
                    Name = mContainedObjectAsIpso.Name;
                }
            }

            mContainedObjectAsIpso = containedObject as IRenderableIpso;

            mContainedObjectAsIVisible = containedObject as IVisible;

            if (mContainedObjectAsIpso != null)
            {
                mContainedObjectAsIpso.Children.CollectionChanged += HandleCollectionChanged;
            }

            if (containedObject != null)
            {
                UpdateLayout();
            }
        }

        public virtual void CreateChildrenRecursively(ElementSave elementSave, ISystemManagers systemManagers)
        {
            bool isScreen = elementSave is ScreenSave;

            foreach (var instance in elementSave.Instances)
            {
                var childGue = instance.ToGraphicalUiElement(systemManagers);

                if (childGue != null)
                {
                    if (!isScreen)
                    {
                        childGue.Parent = this;
                    }
                    childGue.ElementGueContainingThis = this;
                }
            }
        }

        #endregion

        #region Methods

        public virtual void AfterFullCreation()
        {

        }

        /// <summary>
        /// Sets the default state.
        /// </summary>
        /// <remarks>
        /// This function is virtual so that derived classes can override it
        /// and provide a quicker method for setting default states
        /// </remarks>
        public virtual void SetInitialState()
        {
            var elementSave = this.Tag as ElementSave;
            this.SetVariablesRecursively(elementSave, elementSave.DefaultState);
        }

        public void UpdateLayout()
        {
            UpdateLayout(true, true);
        }

        public void UpdateLayout(bool updateParent, bool updateChildren)
        {
            int value = int.MaxValue / 2;
            if (!updateChildren)
            {
                value = 0;
            }
            UpdateLayout(updateParent, value);
        }

        string NameOrType => !string.IsNullOrEmpty(Name) ? Name : $"<{GetType().Name}>";
        
        string ParentQualifiedName => Parent as GraphicalUiElement == null ? NameOrType : (Parent as GraphicalUiElement).ParentQualifiedName + "." + NameOrType;

        public void UpdateLayout(bool updateParent, int childrenUpdateDepth, XOrY? xOrY = null)
        {
            if (updateParent)
            {
                UpdateLayout(ParentUpdateType.All, childrenUpdateDepth, xOrY);
            }
            else
            {
                UpdateLayout(ParentUpdateType.None, childrenUpdateDepth, xOrY);
            }

        }

        public static bool AreUpdatesAppliedWhenInvisible { get; set; } = false;

        HashSet<IRenderableIpso> fullyUpdatedChildren = new HashSet<IRenderableIpso>();
        public void UpdateLayout(ParentUpdateType parentUpdateType, int childrenUpdateDepth, XOrY? xOrY = null)
        {
            var updateParent =
                (parentUpdateType & ParentUpdateType.All) == ParentUpdateType.All ||
                (parentUpdateType & ParentUpdateType.IfParentStacks) == ParentUpdateType.IfParentStacks && GetIfParentStacks() ||
                (parentUpdateType & ParentUpdateType.IfParentWidthHeightDependOnChildren) == ParentUpdateType.IfParentWidthHeightDependOnChildren && (Parent as GraphicalUiElement)?.GetIfDimensionsDependOnChildren() == true;

            #region Early Out - Suspended

            var asIVisible = this as IVisible;

            var isSuspended = mIsLayoutSuspended || IsAllLayoutSuspended;
            if (!isSuspended)
            {
                isSuspended = !AreUpdatesAppliedWhenInvisible && mContainedObjectAsIVisible != null && asIVisible.AbsoluteVisible == false;
            }

            if (isSuspended)
            {
                MakeDirty(parentUpdateType, childrenUpdateDepth, xOrY);
                return;
            }

            if(!AreUpdatesAppliedWhenInvisible)
            {
                var parentAsIVisible = Parent as IVisible;
                if (Visible == false && parentAsIVisible?.AbsoluteVisible == false )
                {
                    return;
                }
            }

            #endregion

            #region Early Out - Update Parent and exit

            currentDirtyState = null;


            // May 15, 2014
            // Parent needs to be
            // set before we start
            // doing the updates because
            // we use foreaches internally
            // in the updates.
            if (mContainedObjectAsIpso != null)
            {
                // If we assign the Parent, then the Parent will have the 
                // mContainedObjectAsIpso added to its children, which will
                // result in it being rendered. But this GraphicalUiElement is
                // already a child of the Parent, so adding the mContainedObjectAsIpso
                // as well would result in a double-render. Instead, we'll set the parent
                // direct, so the parent doesn't know about this child:
                //mContainedObjectAsIpso.Parent = mParent;
                mContainedObjectAsIpso.SetParentDirect(mParent);
            }


            // Not sure why we use the ParentGue and not the Parent itself...
            // We want to do it on the actual Parent so that objects attached to components
            // should update the components
            if (updateParent && GetIfShouldCallUpdateOnParent())
            {
                var asGue = this.Parent as GraphicalUiElement;
                // Just climb up one and update from there
                asGue.UpdateLayout(parentUpdateType, childrenUpdateDepth + 1);
                ChildrenUpdatingParentLayoutCalls++;
                return;
            }
            // This should be *after* the return when updating the parent otherwise we double-count layouts
            UpdateLayoutCallCount++;

            #endregion

            float widthBeforeLayout = 0;
            float heightBeforeLayout = 0;
            float xBeforeLayout = 0;
            float yBeforeLayout = 0;
            // Victor Chelaru
            // March 1, 2015
            // We tested not doing "deep" UpdateLayouts
            // if the object doesn't actually need it. This
            // is the case if the if-statement below evaluates to true. But in practice
            // we got very minor reduction in calls, but we incurred a lot of if-checks, so I don't
            // think this is worth it at this time.
            //if(this.mXOrigin == HorizontalAlignment.Left && mXUnits == GeneralUnitType.PixelsFromSmall &&
            //    this.mYOrigin == VerticalAlignment.Top && mYUnits == GeneralUnitType.PixelsFromSmall &&
            //    this.mWidthUnit == DimensionUnitType.Absolute && this.mWidth > 0 &&
            //    this.mHeightUnit == DimensionUnitType.Absolute && this.mHeight > 0)
            //{
            //    var parent = EffectiveParentGue;
            //    if (parent == null || parent.ChildrenLayout == Gum.Managers.ChildrenLayout.Regular)
            //    {
            //        UnnecessaryUpdateLayouts++;
            //    }
            //}

            float parentWidth;
            float parentHeight;

            GetParentDimensions(out parentWidth, out parentHeight);

            float absoluteParentRotation = 0;
            bool isParentFlippedHorizontally = false;
            if (this.Parent != null)
            {
                absoluteParentRotation = this.Parent.GetAbsoluteRotation();
                isParentFlippedHorizontally = Parent.GetAbsoluteFlipHorizontal();
            }
            else if (this.ElementGueContainingThis != null && this.ElementGueContainingThis.mContainedObjectAsIpso != null)
            {
                parentWidth = this.ElementGueContainingThis.mContainedObjectAsIpso.Width;
                parentHeight = this.ElementGueContainingThis.mContainedObjectAsIpso.Height;

                absoluteParentRotation = this.ElementGueContainingThis.GetAbsoluteRotation();
            }

            if (mContainedObjectAsIpso != null)
            {
                if (mContainedObjectAsIpso is ISetClipsChildren clipsChildrenChild)
                {
                    clipsChildrenChild.ClipsChildren = ClipsChildren;
                }

                if (this.mContainedObjectAsIpso != null)
                {
                    widthBeforeLayout = mContainedObjectAsIpso.Width;
                    heightBeforeLayout = mContainedObjectAsIpso.Height;

                    xBeforeLayout = mContainedObjectAsIpso.X;
                    yBeforeLayout = mContainedObjectAsIpso.Y;
                }

                // The texture dimensions may need to be set before
                // updating width if we are using % of texture width/height.
                // However, if the texture coordinates depend on the dimensions
                // (like for a tiling background) then this also needs to be set
                // after UpdateDimensions. 
                if (mContainedObjectAsIpso is ITextureCoordinate)
                {
                    UpdateTextureCoordinatesNotDimensionBased();
                }

                // August 12, 2021
                // If we can update one
                // of the dimensions first
                // (if it doesn't depend on
                // any children), we should, since
                // it can make the children update have
                // the real width/height set properly
                // May 26, 2023
                // If a dimension doesn't depend on any children, then we are already
                // in a state where we can update that dimension now before doing any children
                // updates. Let's do that.
                var widthDependencyType = this.WidthUnits.GetDependencyType();
                var heightDependencyType = this.HeightUnits.GetDependencyType();

                var hasChildDependency = widthDependencyType == HierarchyDependencyType.DependsOnChildren ||
                    heightDependencyType == HierarchyDependencyType.DependsOnChildren;

                if (widthDependencyType != HierarchyDependencyType.DependsOnChildren && heightDependencyType != HierarchyDependencyType.DependsOnChildren)
                {
                    UpdateDimensions(parentWidth, parentHeight, null, true);
                }
                else if(widthDependencyType != HierarchyDependencyType.DependsOnChildren)
                {
                    UpdateDimensions(parentWidth, parentHeight, XOrY.X, considerWrappedStacked: false);
                }
                if (heightDependencyType != HierarchyDependencyType.DependsOnChildren)
                {
                    UpdateDimensions(parentWidth, parentHeight, XOrY.Y, considerWrappedStacked: false);
                }

                fullyUpdatedChildren.Clear();

                if (hasChildDependency && childrenUpdateDepth > 0)
                {
                    // This causes a double-update of children. For list boxes, this can be expensive.
                    // We can special-case this IF
                    // 1. This depends on children
                    // 2. This stacks in the same axis as the children
                    // 3. This is using FixedStackSpacing
                    // 4. This has more than one child
                    // for now, let's do this only on the vertical axis as a test:
                    if(this.ChildrenLayout == Gum.Managers.ChildrenLayout.TopToBottomStack &&
                        this.HeightUnits.GetDependencyType() == HierarchyDependencyType.DependsOnChildren &&
                        this.UseFixedStackChildrenSize &&
                        this.Children?.Count > 1)
                    {

                        //UpdateDimensions(parentWidth, parentHeight, XOrY.Y, considerWrappedStacked: false);
                        var firstChild = this.Children[0] as GraphicalUiElement;
                        var childLayout = firstChild.GetChildLayoutType(this);

                        if(childLayout == ChildType.Absolute)
                        {
                            firstChild?.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1);
                            fullyUpdatedChildren.Add(firstChild);
                        }
                        else
                        {
                            firstChild?.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1, XOrY.Y);
                        }
                    }
                    else
                    {
                        UpdateChildren(childrenUpdateDepth, ChildType.Absolute, skipIgnoreByParentSize:true, newlyUpdated:fullyUpdatedChildren);
                    }
                }

                // This will update according to all absolute children
                // Now that the children have been updated, we can do any dimensions that still need updating based on the children changes:

                if (widthDependencyType == HierarchyDependencyType.DependsOnChildren)
                {
                    UpdateDimensions(parentWidth, parentHeight, XOrY.X, considerWrappedStacked: false);
                }
                if (heightDependencyType == HierarchyDependencyType.DependsOnChildren)
                {
                    UpdateDimensions(parentWidth, parentHeight, XOrY.Y, considerWrappedStacked: false);
                }

                if (this.WrapsChildren && (this.ChildrenLayout == ChildrenLayout.LeftToRightStack || this.ChildrenLayout == ChildrenLayout.TopToBottomStack))
                {
                    // Now we can update all children that are wrapped:
                    UpdateChildren(childrenUpdateDepth, ChildType.StackedWrapped, skipIgnoreByParentSize: false);
                    if (this.WidthUnits.GetDependencyType() == HierarchyDependencyType.DependsOnChildren ||
                        this.HeightUnits.GetDependencyType() == HierarchyDependencyType.DependsOnChildren)
                    {
                        UpdateDimensions(parentWidth, parentHeight, xOrY, considerWrappedStacked: true);
                    }
                }

                if (mContainedObjectAsIpso is ITextureCoordinate)
                {
                    UpdateTextureCoordinatesDimensionBased();
                }

                // If the update is "deep" then we want to refresh the text texture.
                // Otherwise it may have been something shallow like a reposition.
                // -----------------------------------------------------------------------------
                // Update December 3, 2022 - This if-check causes lots of performance issues
                // If a text object is updating itself and its parent needs to update, then if
                // children depth > 0, then the parent update will cause all other children to update
                // which is very expensive. We now do enough checks at the property level to prevent the
                // text from updating unnecessarily, so let's change this to prevent parents from updating
                // all of their children:
                //if (mContainedObjectAsIpso is Text asText && childrenUpdateDepth > 0)
                if (mContainedObjectAsIpso is IText asText)
                {
                    // Only if the width or height have changed:
                    if (mContainedObjectAsIpso.Width != widthBeforeLayout ||
                        mContainedObjectAsIpso.Height != heightBeforeLayout)
                    {
                        asText.SetNeedsRefreshToTrue();
                        asText.UpdatePreRenderDimensions();
                    }
                }

                // See the above call to UpdateTextureCoordiantes
                // on why this is called both before and after UpdateDimensions
                if (mContainedObjectAsIpso is ITextureCoordinate)
                {
                    UpdateTextureCoordinatesNotDimensionBased();
                }


                UpdatePosition(parentWidth, parentHeight, xOrY, absoluteParentRotation, isParentFlippedHorizontally);

                if (GetIfParentStacks())
                {
                    RefreshParentRowColumnDimensionForThis();
                }

                if (this.Parent == null)
                {
                    mContainedObjectAsIpso.Rotation = mRotation;
                }
                else
                {
                    if (isParentFlippedHorizontally)
                    {
                        mContainedObjectAsIpso.Rotation =
                            -mRotation;// + Parent.GetAbsoluteRotation();
                    }
                    else
                    {
                        mContainedObjectAsIpso.Rotation =
                            mRotation;// + Parent.GetAbsoluteRotation();
                    }
                }

            }

            if (childrenUpdateDepth > 0)
            {
                UpdateChildren(childrenUpdateDepth, ChildType.All, skipIgnoreByParentSize:false, alreadyUpdated: fullyUpdatedChildren);

                var sizeDependsOnChildren = this.WidthUnits == DimensionUnitType.RelativeToChildren ||
                    this.HeightUnits == DimensionUnitType.RelativeToChildren;

                var canOneDimensionChangeOtherDimension = false;

                if (this.mContainedObjectAsIpso == null)
                {
                    for(int i = 0; i < this.mWhatThisContains.Count; i++)
                    {
                        canOneDimensionChangeOtherDimension = GetIfOneDimensionCanChangeOtherDimension(mWhatThisContains[i]);

                        if (canOneDimensionChangeOtherDimension)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < this.Children.Count; i++)
                    {
                        var uncastedChild = Children[i];

                        if (uncastedChild is GraphicalUiElement child)
                        {
                            canOneDimensionChangeOtherDimension = GetIfOneDimensionCanChangeOtherDimension(child);

                            if (canOneDimensionChangeOtherDimension)
                            {
                                break;
                            }

                        }
                    }
                }

                if (sizeDependsOnChildren && canOneDimensionChangeOtherDimension)
                {
                    float widthBeforeSecondLayout = mContainedObjectAsIpso.Width;
                    float heightBeforeSecondLayout = mContainedObjectAsIpso.Height;

                    UpdateDimensions(parentWidth, parentHeight, xOrY, considerWrappedStacked: true);

                    if (widthBeforeSecondLayout != mContainedObjectAsIpso.Width ||
                        heightBeforeSecondLayout != mContainedObjectAsIpso.Height)
                    {
                        UpdateChildren(childrenUpdateDepth, ChildType.BothAbsoluteAndRelative, skipIgnoreByParentSize:true);
                    }

                }
            }

            // Eventually add more conditions here to make it fire less often
            // like check the width/height of the parent to see if they're 0
            if (updateParent && GetIfShouldCallUpdateOnParent())
            {
                (this.Parent as GraphicalUiElement).UpdateLayout(false, false);
                ChildrenUpdatingParentLayoutCalls++;
            }
            if (this.mContainedObjectAsIpso != null)
            {
                if (widthBeforeLayout != mContainedObjectAsIpso.Width ||
                    heightBeforeLayout != mContainedObjectAsIpso.Height)
                {
                    if(!isInSizeChange)
                    {
                        isInSizeChange = true;
                        SizeChanged?.Invoke(this, null);
                        isInSizeChange = false;
                    }
                }

                if (xBeforeLayout != mContainedObjectAsIpso.X ||
                        yBeforeLayout != mContainedObjectAsIpso.Y)
                {
                    PositionChanged?.Invoke(this, null);
                }
            }

        }

        ChildType GetChildLayoutType(GraphicalUiElement parent)
        {
            var doesParentWrapStack = parent.WrapsChildren && (parent.ChildrenLayout == ChildrenLayout.LeftToRightStack || parent.ChildrenLayout == ChildrenLayout.TopToBottomStack);

            var parentWidthDependencyType = parent.WidthUnits.GetDependencyType();
            var parentHeightDependencyType = parent.HeightUnits.GetDependencyType();

            var isParentWidthNoDependencyOrOnParent = parentWidthDependencyType == HierarchyDependencyType.NoDependency || parentWidthDependencyType == HierarchyDependencyType.DependsOnParent;
            var isParentHeightNoDependencyOrOnParent = parentHeightDependencyType == HierarchyDependencyType.NoDependency || parentHeightDependencyType == HierarchyDependencyType.DependsOnParent;

            var isAbsolute = (mWidthUnit.GetDependencyType() != HierarchyDependencyType.DependsOnParent || isParentWidthNoDependencyOrOnParent) &&
                            (mHeightUnit.GetDependencyType() != HierarchyDependencyType.DependsOnParent || isParentHeightNoDependencyOrOnParent) &&
                            (mWidthUnit.GetDependencyType() != HierarchyDependencyType.DependsOnSiblings) &&
                            (mHeightUnit.GetDependencyType() != HierarchyDependencyType.DependsOnSiblings) &&

                (mXUnits == GeneralUnitType.PixelsFromSmall || 
                 (mXUnits == GeneralUnitType.PixelsFromMiddle && isParentWidthNoDependencyOrOnParent) ||
                 (mXUnits == GeneralUnitType.PixelsFromLarge && isParentWidthNoDependencyOrOnParent) || 
                 (mXUnits == GeneralUnitType.PixelsFromMiddleInverted && isParentWidthNoDependencyOrOnParent)) &&
                
                (mYUnits == GeneralUnitType.PixelsFromSmall || 
                 (mYUnits == GeneralUnitType.PixelsFromMiddle && isParentHeightNoDependencyOrOnParent) ||
                 (mYUnits == GeneralUnitType.PixelsFromLarge && isParentHeightNoDependencyOrOnParent) || 
                 (mYUnits == GeneralUnitType.PixelsFromMiddleInverted && isParentHeightNoDependencyOrOnParent) ||
                 mYUnits == GeneralUnitType.PixelsFromBaseline);

            if (doesParentWrapStack)
            {
                return isAbsolute ? ChildType.StackedWrapped : ChildType.Relative;
            }
            else
            {
                return isAbsolute ? ChildType.Absolute : ChildType.Relative;
            }
        }

        ChildType GetChildLayoutType(XOrY xOrY, GraphicalUiElement parent)
        {
            bool isAbsolute;
            var doesParentWrapStack = parent.WrapsChildren && (parent.ChildrenLayout == ChildrenLayout.LeftToRightStack || parent.ChildrenLayout == ChildrenLayout.TopToBottomStack);

            if (xOrY == XOrY.X)
            {
                var widthUnitDependencyType = mWidthUnit.GetDependencyType();
                isAbsolute = (widthUnitDependencyType != HierarchyDependencyType.DependsOnParent || this.WidthUnits.GetDependencyType() == HierarchyDependencyType.NoDependency ) &&
                    (mXUnits == GeneralUnitType.PixelsFromLarge || mXUnits == GeneralUnitType.PixelsFromMiddle ||
                        mXUnits == GeneralUnitType.PixelsFromSmall || mXUnits == GeneralUnitType.PixelsFromMiddleInverted);

            }
            else // Y
            {
                isAbsolute = (mHeightUnit.GetDependencyType() != HierarchyDependencyType.DependsOnParent || this.HeightUnits.GetDependencyType() == HierarchyDependencyType.NoDependency) &&
                    (mYUnits == GeneralUnitType.PixelsFromLarge || mYUnits == GeneralUnitType.PixelsFromMiddle ||
                        mYUnits == GeneralUnitType.PixelsFromSmall || mYUnits == GeneralUnitType.PixelsFromMiddleInverted &&
                        mYUnits == GeneralUnitType.PixelsFromBaseline);

            }

            if (doesParentWrapStack)
            {
                return isAbsolute ? ChildType.StackedWrapped : ChildType.Relative;
            }
            else
            {
                return isAbsolute ? ChildType.Absolute : ChildType.Relative;
            }
        }

        float GetRequiredParentWidth()
        {
            var effectiveParent = this.EffectiveParentGue;
            if (effectiveParent != null && effectiveParent.ChildrenLayout == ChildrenLayout.TopToBottomStack && effectiveParent.WrapsChildren)
            {
                var asIpso = this as IPositionedSizedObject;
                return asIpso.X + asIpso.Width;
            }
            else
            {
                float positionValue = mX;

                // This GUE hasn't been set yet so it can't give
                // valid widths/heights
                if (this.mContainedObjectAsIpso == null)
                {
                    return 0;
                }
                float smallEdge = positionValue;
                if (mXOrigin == HorizontalAlignment.Center)
                {
                    smallEdge = positionValue - ((IPositionedSizedObject)this).Width / 2.0f;
                }
                else if (mXOrigin == HorizontalAlignment.Right)
                {
                    smallEdge = positionValue - ((IPositionedSizedObject)this).Width;
                }

                float bigEdge = positionValue;
                if (mXOrigin == HorizontalAlignment.Center)
                {
                    bigEdge = positionValue + ((IPositionedSizedObject)this).Width / 2.0f;
                }
                if (mXOrigin == HorizontalAlignment.Left)
                {
                    bigEdge = positionValue + ((IPositionedSizedObject)this).Width;
                }

                var units = mXUnits;

                float dimensionToReturn = GetDimensionFromEdges(smallEdge, bigEdge, units);

                return dimensionToReturn;
            }
        }

        float GetRequiredParentHeight()
        {
            var effectiveParent = this.EffectiveParentGue;
            if (effectiveParent != null && effectiveParent.ChildrenLayout == ChildrenLayout.LeftToRightStack && effectiveParent.WrapsChildren)
            {
                var asIpso = this as IPositionedSizedObject;
                return asIpso.Y + asIpso.Height;
            }
            else
            {
                float positionValue = mY;

                // This GUE hasn't been set yet so it can't give
                // valid widths/heights
                if (this.mContainedObjectAsIpso == null)
                {
                    return 0;
                }
                float smallEdge = positionValue;

                var units = mYUnits;
                if (units == GeneralUnitType.PixelsFromMiddleInverted)
                {
                    smallEdge *= -1;
                }

                if (mYOrigin == VerticalAlignment.Center)
                {
                    smallEdge = positionValue - ((IPositionedSizedObject)this).Height / 2.0f;
                }
                else if (mYOrigin == VerticalAlignment.TextBaseline)
                {
                    if (mContainedObjectAsIpso is IText text)
                    {
                        smallEdge = positionValue - ((IPositionedSizedObject)this).Height + text.DescenderHeight * text.FontScale;
                    }
                    else
                    {
                        smallEdge = positionValue - ((IPositionedSizedObject)this).Height;
                    }
                }
                else if (mYOrigin == VerticalAlignment.Bottom)
                {
                    smallEdge = positionValue - ((IPositionedSizedObject)this).Height;
                }

                float bigEdge = positionValue;
                if (mYOrigin == VerticalAlignment.Center)
                {
                    bigEdge = positionValue + ((IPositionedSizedObject)this).Height / 2.0f;
                }
                if (mYOrigin == VerticalAlignment.Top)
                {
                    bigEdge = positionValue + ((IPositionedSizedObject)this).Height;
                }

                float dimensionToReturn = GetDimensionFromEdges(smallEdge, bigEdge, units);

                return dimensionToReturn;
            }

        }

        private static float GetDimensionFromEdges(float smallEdge, float bigEdge, GeneralUnitType units)
        {
            float dimensionToReturn = 0;
            if (units == GeneralUnitType.PixelsFromSmall)
            // The value already comes in properly inverted
            {
                smallEdge = 0;

                bigEdge = System.Math.Max(0, bigEdge);
                dimensionToReturn = bigEdge - smallEdge;
            }
            else if (units == GeneralUnitType.PixelsFromMiddle ||
                units == GeneralUnitType.PixelsFromMiddleInverted)
            {
                // use the full width
                float abs1 = System.Math.Abs(smallEdge);
                float abs2 = System.Math.Abs(bigEdge);

                dimensionToReturn = 2 * System.Math.Max(abs1, abs2);
            }
            else if (units == GeneralUnitType.PixelsFromLarge)
            {
                smallEdge = System.Math.Min(0, smallEdge);
                bigEdge = 0;
                dimensionToReturn = bigEdge - smallEdge;

            }
            return dimensionToReturn;
        }

        public bool GetIfDimensionsDependOnChildren()
        {
            // If this is a Screen, then it doesn't have a size. Screens cannot depend on children:
            bool isScreen = ElementSave != null && ElementSave is ScreenSave;
            return !isScreen &&
                (this.WidthUnits.GetDependencyType() == HierarchyDependencyType.DependsOnChildren ||
                this.HeightUnits.GetDependencyType() == HierarchyDependencyType.DependsOnChildren);
        }

        public virtual void PreRender()
        {
            if (mContainedObjectAsIpso != null)
            {
                mContainedObjectAsIpso.PreRender();
            }
        }

        bool GetIfShouldCallUpdateOnParent()
        {
            var asGue = this.Parent as GraphicalUiElement;

            if (asGue != null)
            {
                var shouldUpdateParent =  
                    // parent needs to be resized based on this position or size
                    asGue.GetIfDimensionsDependOnChildren() || 
                    // parent stacks its children, so siblings need to adjust their position based on this
                    asGue.ChildrenLayout != Gum.Managers.ChildrenLayout.Regular;

                if(!shouldUpdateParent)
                {
                    // if any siblings are ratio-based, then we need to
                    if (this.Parent == null)
                    {
                        for (int i = 0; i < this.ElementGueContainingThis.mWhatThisContains.Count; i++)
                        {
                            var sibling = this.ElementGueContainingThis.mWhatThisContains[i];
                            if(sibling.WidthUnits == DimensionUnitType.Ratio || sibling.HeightUnits == DimensionUnitType.Ratio)
                            {
                                return true;
                            }
                        }
                    }
                    else if (this.Parent is GraphicalUiElement parentGue && parentGue.Children != null)
                    {
                        var siblingsAsIpsos = parentGue.Children;
                        for (int i = 0; i < siblingsAsIpsos.Count; i++)
                        {
                            var siblingAsGraphicalUiElement = siblingsAsIpsos[i] as GraphicalUiElement;
                            if(siblingAsGraphicalUiElement.WidthUnits == DimensionUnitType.Ratio || siblingAsGraphicalUiElement.HeightUnits == DimensionUnitType.Ratio)
                            {
                                return true;
                            }
                        }
                    }

                }
                return shouldUpdateParent;
            }
            else
            {
                return false;
            }
        }

        private static bool GetIfOneDimensionCanChangeOtherDimension(GraphicalUiElement gue)
        {
            var canOneDimensionChangeTheOtherOnChild = gue.RenderableComponent is IText ||
                    gue.WidthUnits == DimensionUnitType.PercentageOfOtherDimension ||
                    gue.HeightUnits == DimensionUnitType.PercentageOfOtherDimension ||
                    gue.WidthUnits == DimensionUnitType.MaintainFileAspectRatio ||
                    gue.HeightUnits == DimensionUnitType.MaintainFileAspectRatio ||


                    ((gue.ChildrenLayout == ChildrenLayout.LeftToRightStack || gue.ChildrenLayout == ChildrenLayout.TopToBottomStack) && gue.WrapsChildren);

            // If the child cannot be directly changed by a dimension, it may be indirectly changed by a dimension recursively. This can happen
            // if the child either depends on its own children's widths and heights, and one of its children can have its dimension changed.

            if (!canOneDimensionChangeTheOtherOnChild && gue.GetIfDimensionsDependOnChildren())
            {
                for (int i = 0; i < gue.Children.Count; i++)
                {
                    var uncastedChild = gue.Children[i];

                    if (uncastedChild is GraphicalUiElement child)
                    {

                        if (GetIfOneDimensionCanChangeOtherDimension(child))
                        {
                            canOneDimensionChangeTheOtherOnChild = true;
                            break;
                        }
                    }
                }
            }

            return canOneDimensionChangeTheOtherOnChild;

        }

        // Records the type of update needed when layout resumes
        private void MakeDirty(ParentUpdateType parentUpdateType, int childrenUpdateDepth, XOrY? xOrY)
        {
            if (currentDirtyState == null)
            {
                currentDirtyState = new DirtyState();

                currentDirtyState.XOrY = xOrY;
            }

            currentDirtyState.ParentUpdateType = currentDirtyState.ParentUpdateType | parentUpdateType;
            currentDirtyState.ChildrenUpdateDepth = Math.Max(
                currentDirtyState.ChildrenUpdateDepth, childrenUpdateDepth);

            // If the update is supposed to update all associations, make it null...
            if (xOrY == null)
            {
                currentDirtyState.XOrY = null;
            }
            // If neither are null and they differ, then that means update both, so set it to null
            else if (currentDirtyState.XOrY != null && currentDirtyState.XOrY != xOrY)
            {
                currentDirtyState.XOrY = null;
            }
            // It's not possible to set either X or Y here. That can only happen on initialization
            // of the currentDirtyState
        }

        private void RefreshParentRowColumnDimensionForThis()
        {
            // If it stacks, then update this row/column's dimensions given the index of this
            var indexToUpdate = this.StackedRowOrColumnIndex;

            if (indexToUpdate == -1)
            {
                return;
            }

            var parentGue = EffectiveParentGue;

            if (this.Visible)
            {

                if (parentGue.StackedRowOrColumnDimensions == null)
                {
                    parentGue.StackedRowOrColumnDimensions = new List<float>();
                }

                if (parentGue.StackedRowOrColumnDimensions.Count <= indexToUpdate)
                {
                    parentGue.StackedRowOrColumnDimensions.Add(0);
                }
                else
                {
                    if (indexToUpdate >= 0 && indexToUpdate < parentGue.StackedRowOrColumnDimensions.Count)
                    {
                        parentGue.StackedRowOrColumnDimensions[indexToUpdate] = 0;
                    }
                }
                foreach (GraphicalUiElement child in parentGue.Children)
                {
                    if (child.Visible)
                    {
                        var asIpso = child as IPositionedSizedObject;


                        if (child.StackedRowOrColumnIndex == indexToUpdate)
                        {
                            if (parentGue.ChildrenLayout == ChildrenLayout.LeftToRightStack)
                            {
                                parentGue.StackedRowOrColumnDimensions[indexToUpdate] =
                                    System.Math.Max(parentGue.StackedRowOrColumnDimensions[indexToUpdate],
                                    child.Y + child.GetAbsoluteHeight());
                            }
                            else
                            {
                                parentGue.StackedRowOrColumnDimensions[indexToUpdate] =
                                    System.Math.Max(parentGue.StackedRowOrColumnDimensions[indexToUpdate],
                                    child.X + child.GetAbsoluteWidth());
                            }

                            // We don't need to worry about the children after this, because the siblings will get updated in order:
                            // This can (on average) make this run 2x as fast
                            if (this == child)
                            {
                                break;
                            }
                        }
                    }
                }
            }

        }

        private void UpdateChildren(int childrenUpdateDepth, ChildType childrenUpdateType, bool skipIgnoreByParentSize, HashSet<IRenderableIpso> alreadyUpdated = null, HashSet<IRenderableIpso> newlyUpdated = null)
        {
            bool CanDoFullUpdate(ChildType thisChildUpdateType, GraphicalUiElement childGue)
            {

                if(skipIgnoreByParentSize && childGue.IgnoredByParentSize)
                {
                    return false;
                }

                return
                    childrenUpdateType == ChildType.All ||
                    (childrenUpdateType == ChildType.Absolute && thisChildUpdateType == ChildType.Absolute) ||
                    (childrenUpdateType == ChildType.Relative && (thisChildUpdateType == ChildType.Relative || thisChildUpdateType == ChildType.BothAbsoluteAndRelative)) ||
                    (childrenUpdateType == ChildType.StackedWrapped && thisChildUpdateType == ChildType.StackedWrapped);
            }
            if (this.mContainedObjectAsIpso == null)
            {
                for(int i = 0; i < mWhatThisContains.Count; i++)
                {
                    var child = mWhatThisContains[i];
                    // Victor Chelaru
                    // January 10, 2017
                    // I think we may not want to update any children which
                    // have parents, because they'll get updated through their
                    // parents...
                    if (child.Parent == null || child.Parent == this)
                    {
                        if (CanDoFullUpdate(child.GetChildLayoutType(this), child))
                        {
                            child.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1);
                            newlyUpdated?.Add(child);
                        }
                        else
                        {
                            // only update absolute layout, and the child has some relative values, but let's see if 
                            // we can do only one axis:
                            if (CanDoFullUpdate(child.GetChildLayoutType(XOrY.X, this), child))
                            {
                                child.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1, XOrY.X);
                            }
                            else if (CanDoFullUpdate(child.GetChildLayoutType(XOrY.Y, this), child))
                            {
                                child.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1, XOrY.Y);
                            }
                        }
                    }
                }
            }
            else
            {
                // 7/17/2023 - Long explanation about this code:
                // Normally children updating can be done in index order. However, if a child uses Ratio width or height, then the 
                // height of this child depends on its siblings. Since it depends on its siblings, any sibling needs to update first 
                // if it is using a complex WidthUnit or HeightUnit. All other update types (such as absolute) can be determined on the
                // spot when calculating the width of the ratio child.
                // Therefore, we will need to do all RelativeToChildren first if:
                //
                // * Some children use WidthUnits with Ratio, and some children use WidthUnits with RelativeToChildren
                //   --or--
                // * Any children use HeightUnits with Ratios, and some children use HeightUnits with RelativeToChildren
                //
                // If either is the case, then we will first update all children that have the relative properties. Then we'll loop through all of them
                // Note about optimization - if children using relative all come first, then a normal order will satisfy the dependencies.
                // But that makes the code slightly more complex, so I'll bother with that performance optimization later.

                bool useRatioWidth = false;
                bool useRatioHeight = false;
                bool useRelativeChildrenWidth = false;
                bool useRelativeChildrenHeight = false;

                for (int i = 0; i < this.Children.Count; i++)
                {
                    var child = this.Children[i] as GraphicalUiElement;

                    useRatioWidth |= child.WidthUnits == DimensionUnitType.Ratio;
                    useRatioHeight |= child.HeightUnits == DimensionUnitType.Ratio;

                    useRelativeChildrenWidth |= child.WidthUnits == DimensionUnitType.RelativeToChildren;
                    useRelativeChildrenHeight |= child.HeightUnits == DimensionUnitType.RelativeToChildren;
                }

                var shouldUpdateRelativeFirst = (useRatioWidth && useRelativeChildrenWidth) || (useRatioHeight && useRelativeChildrenHeight);

                // Update - if this item stacks, then it cannot mark the children as updated - it needs to do another
                // pass later to update the position of the children in order from top-to-bottom. If we flag as updated,
                // then the pass later that does the actual stacking will skip anything that is flagged as updated.
                // This bug was reproduced as reported in this issue:
                // https://github.com/vchelaru/Gum/issues/141
                var shouldFlagAsUpdated = this.ChildrenLayout == ChildrenLayout.Regular;

                if(shouldUpdateRelativeFirst)
                {
                    for (int i = 0; i < this.Children.Count; i++)
                    {
                        var ipsoChild = this.Children[i];

                        if ((alreadyUpdated == null || alreadyUpdated.Contains(ipsoChild) == false) && ipsoChild is GraphicalUiElement child)
                        {
                            if(child.WidthUnits == DimensionUnitType.RelativeToChildren || child.HeightUnits == DimensionUnitType.RelativeToChildren)
                            {
                                UpdateChild(child, flagAsUpdated:false);
                            }
                        }
                    }

                    for (int i = 0; i < this.Children.Count; i++)
                    {
                        var ipsoChild = this.Children[i];

                        if ((alreadyUpdated == null || alreadyUpdated.Contains(ipsoChild) == false) && ipsoChild is GraphicalUiElement child)
                        {
                            // now do all:
                            UpdateChild(child, flagAsUpdated: shouldFlagAsUpdated);
                        }
                    }
                }
                else
                {
                    // do a normal one:
                    for (int i = 0; i < this.Children.Count; i++)
                    {
                        var ipsoChild = this.Children[i];

                        if ((alreadyUpdated == null || alreadyUpdated.Contains(ipsoChild) == false) && ipsoChild is GraphicalUiElement child)
                        {
                            // now do all:
                            UpdateChild(child, flagAsUpdated: shouldFlagAsUpdated);
                        }
                    }
                }


                void UpdateChild(GraphicalUiElement child, bool flagAsUpdated)
                {
                    
                    var canDoFullUpdate =
                        CanDoFullUpdate(child.GetChildLayoutType(this), child);


                    if (canDoFullUpdate)
                    {
                        child.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1);
                        if(flagAsUpdated)
                        {
                            newlyUpdated?.Add(child);
                        }
                    }
                    else
                    {
                        // only update absolute layout, and the child has some relative values, but let's see if 
                        // we can do only one axis:
                        if (CanDoFullUpdate(child.GetChildLayoutType(XOrY.X, this), child))
                        {
                            // todo - maybe look at the code below to see if we need to do the same thing here for
                            // width/height updates:
                            child.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1, XOrY.X);
                        }
                        else if (CanDoFullUpdate(child.GetChildLayoutType(XOrY.Y, this), child))
                        {
                            // in this case, the child's Y is going to be updated, but the child's X may depend on 
                            // the parent's width. If so, the parent's width should already be updated, so long as
                            // the width doesn't depend on the children. So...let's see if that's the case:
                            var widthDependencyType = this.WidthUnits.GetDependencyType();
                            if (widthDependencyType != HierarchyDependencyType.DependsOnChildren &&
                                (child.HeightUnits == DimensionUnitType.PercentageOfOtherDimension) || (child.HeightUnits == DimensionUnitType.MaintainFileAspectRatio))
                            {
                                child.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1);
                            }
                            else
                            {
                                child.UpdateLayout(ParentUpdateType.None, childrenUpdateDepth - 1, XOrY.Y);

                            }
                        }
                    }
                    
                }

            }
        }

        private void GetParentDimensions(out float parentWidth, out float parentHeight)
        {
            parentWidth = CanvasWidth;
            parentHeight = CanvasHeight;

            // I think we want to obey the non GUE parent first if it exists, then the GUE
            //if (this.ParentGue != null && this.ParentGue.mContainedObjectAsRenderable != null)
            //{
            //    parentWidth = this.ParentGue.mContainedObjectAsIpso.Width;
            //    parentHeight = this.ParentGue.mContainedObjectAsIpso.Height;
            //}
            //else if (this.Parent != null)
            //{
            //    parentWidth = Parent.Width;
            //    parentHeight = Parent.Height;
            //}

            if (this.Parent != null)
            {
                if(Parent is GraphicalUiElement parentGue && (parentGue.ChildrenLayout == ChildrenLayout.AutoGridVertical || parentGue.ChildrenLayout == ChildrenLayout.AutoGridHorizontal ))
                {
                    var horizontalCells = parentGue.AutoGridHorizontalCells;
                    if (horizontalCells < 1) horizontalCells = 1;
                    var verticalCells = parentGue.AutoGridVerticalCells;
                    if(verticalCells < 1) verticalCells = 1;

                    parentWidth = parentGue.GetAbsoluteWidth() / horizontalCells;
                    parentHeight = parentGue.GetAbsoluteHeight() / verticalCells;
                }
                else
                {
                    parentWidth = Parent.Width;
                    parentHeight = Parent.Height;
                }
            }
            else if (this.ElementGueContainingThis != null && this.ElementGueContainingThis.mContainedObjectAsIpso != null)
            {
                parentWidth = this.ElementGueContainingThis.mContainedObjectAsIpso.Width;
                parentHeight = this.ElementGueContainingThis.mContainedObjectAsIpso.Height;
            }

#if DEBUG
            if (float.IsPositiveInfinity(parentHeight))
            {
                throw new Exception();
            }
#endif
        }

        private void UpdateTextureCoordinatesDimensionBased()
        {
            int left = mTextureLeft;
            int top = mTextureTop;
            int width = (int)(mContainedObjectAsIpso.Width / mTextureWidthScale);
            int height = (int)(mContainedObjectAsIpso.Height / mTextureHeightScale);

            if (mContainedObjectAsIpso is ITextureCoordinate containedTextureCoordinateObject)
            {
                switch (mTextureAddress)
                {
                    case TextureAddress.DimensionsBased:
                        containedTextureCoordinateObject.SourceRectangle = new Rectangle(
                            left,
                            top,
                            width,
                            height);
                        containedTextureCoordinateObject.Wrap = mWrap;
                        break;
                }
            }
        }

        private void UpdateTextureCoordinatesNotDimensionBased()
        {
            if (mContainedObjectAsIpso is ITextureCoordinate textureCoordinateObject)
            {
                var textureAddress = mTextureAddress;
                switch (textureAddress)
                {
                    case TextureAddress.EntireTexture:
                        textureCoordinateObject.SourceRectangle = null;
                        textureCoordinateObject.Wrap = false;
                        break;
                    case TextureAddress.Custom:
                        textureCoordinateObject.SourceRectangle = new Rectangle(
                            mTextureLeft,
                            mTextureTop,
                            mTextureWidth,
                            mTextureHeight);
                        textureCoordinateObject.Wrap = mWrap;
                        break;
                    case TextureAddress.DimensionsBased:
                        // This is done *after* setting dimensions

                        break;
                }
            }
        }

        private void UpdatePosition(float parentWidth, float parentHeight, XOrY? xOrY, float parentAbsoluteRotation, bool isParentFlippedHorizontally)
        {
            // First get the position of the object without considering if this object should be wrapped.
            // This call may result in the object being placed outside of its parent's bounds. In which case
            // it will be wrapped....later
            UpdatePosition(parentWidth, parentHeight, isParentFlippedHorizontally, shouldWrap: false, xOrY: xOrY, parentRotation: parentAbsoluteRotation);

            var effectiveParent = EffectiveParentGue;

            // Wrap the object if:
            bool shouldWrap =
                effectiveParent != null &&
            // * The parent stacks
                effectiveParent.ChildrenLayout != Gum.Managers.ChildrenLayout.Regular &&

                // * And the parent wraps
                effectiveParent.WrapsChildren &&

                // * And the object is outside of parent's bounds
                ((effectiveParent.ChildrenLayout == Gum.Managers.ChildrenLayout.LeftToRightStack && this.GetAbsoluteRight() > effectiveParent.GetAbsoluteRight()) ||
                (effectiveParent.ChildrenLayout == Gum.Managers.ChildrenLayout.TopToBottomStack && this.GetAbsoluteBottom() > effectiveParent.GetAbsoluteBottom()));

            if (shouldWrap)
            {
                UpdatePosition(parentWidth, parentHeight, isParentFlippedHorizontally, shouldWrap, xOrY: xOrY, parentRotation: parentAbsoluteRotation);
            }
        }

        private void UpdatePosition(float parentWidth, float parentHeight, bool isParentFlippedHorizontally, bool shouldWrap, XOrY? xOrY, float parentRotation)
        {
#if DEBUG
            if (float.IsPositiveInfinity(parentHeight) || float.IsNegativeInfinity(parentHeight))
            {
                throw new ArgumentException(nameof(parentHeight));
            }
            if (float.IsPositiveInfinity(parentHeight) || float.IsNegativeInfinity(parentHeight))
            {
                throw new ArgumentException(nameof(parentHeight));
            }

#endif

            float parentOriginOffsetX;
            float parentOriginOffsetY;
            bool wasHandledX;
            bool wasHandledY;

            bool canWrap = EffectiveParentGue != null && EffectiveParentGue.WrapsChildren;

            GetParentOffsets(canWrap, shouldWrap, parentWidth, parentHeight, isParentFlippedHorizontally,
                out parentOriginOffsetX, out parentOriginOffsetY,
                out wasHandledX, out wasHandledY);


            float unitOffsetX = 0;
            float unitOffsetY = 0;

            AdjustOffsetsByUnits(parentWidth, parentHeight, isParentFlippedHorizontally, xOrY, ref unitOffsetX, ref unitOffsetY);
#if DEBUG
            if (float.IsNaN(unitOffsetX))
            {
                throw new Exception("Invalid unitOffsetX: " + unitOffsetX);
            }

            if (float.IsNaN(unitOffsetY))
            {
                throw new Exception("Invalid unitOffsetY: " + unitOffsetY);
            }
#endif



            AdjustOffsetsByOrigin(isParentFlippedHorizontally, ref unitOffsetX, ref unitOffsetY);
#if DEBUG
            if (float.IsNaN(unitOffsetX) || float.IsNaN(unitOffsetY))
            {
                throw new Exception("Invalid unit offsets");
            }
#endif

            unitOffsetX += parentOriginOffsetX;
            unitOffsetY += parentOriginOffsetY;

            if (parentRotation != 0)
            {
                GetRightAndUpFromRotation(parentRotation, out Vector3 right, out Vector3 up);


                var rotatedOffset = unitOffsetX * right + unitOffsetY * up;


                unitOffsetX = rotatedOffset.X;
                unitOffsetY = rotatedOffset.Y;

            }


            // See if we're explicitly updating only Y. If so, skip setting X.
            if (xOrY != XOrY.Y)
            {
                this.mContainedObjectAsIpso.X = unitOffsetX;
            }

            // See if we're explicitly updating only X. If so, skip setting Y.
            if (xOrY != XOrY.X)
            {
                this.mContainedObjectAsIpso.Y = unitOffsetY;
            }
        }

        public void GetParentOffsets(out float parentOriginOffsetX, out float parentOriginOffsetY)
        {
            float parentWidth;
            float parentHeight;
            GetParentDimensions(out parentWidth, out parentHeight);

            bool throwaway1;
            bool throwaway2;

            bool canWrap = false;
            var effectiveParent = EffectiveParentGue;
            bool isParentFlippedHorizontally = false;
            if (effectiveParent != null)
            {
                canWrap = effectiveParent.WrapsChildren;
                isParentFlippedHorizontally = effectiveParent.GetAbsoluteFlipHorizontal();
            }


            // indicating false to wrap will reset the index on this. We don't want this method
            // to modify anything so store it off and resume:
            var oldIndex = StackedRowOrColumnIndex;


            GetParentOffsets(canWrap, false, parentWidth, parentHeight, isParentFlippedHorizontally, out parentOriginOffsetX, out parentOriginOffsetY,
                out throwaway1, out throwaway2);

            StackedRowOrColumnIndex = oldIndex;

        }

        private void GetParentOffsets(bool canWrap, bool shouldWrap, float parentWidth, float parentHeight, bool isParentFlippedHorizontally, out float parentOriginOffsetX, out float parentOriginOffsetY,
            out bool wasHandledX, out bool wasHandledY)
        {
            parentOriginOffsetX = 0;
            parentOriginOffsetY = 0;

            TryAdjustOffsetsByParentLayoutType(canWrap, shouldWrap, ref parentOriginOffsetX, ref parentOriginOffsetY);

            wasHandledX = false;
            wasHandledY = false;

            AdjustParentOriginOffsetsByUnits(parentWidth, parentHeight, isParentFlippedHorizontally, ref parentOriginOffsetX, ref parentOriginOffsetY,
                ref wasHandledX, ref wasHandledY);

        }

        private void TryAdjustOffsetsByParentLayoutType(bool canWrap, bool shouldWrap, ref float unitOffsetX, ref float unitOffsetY)
        {


            if (GetIfParentStacks())
            {
                float whatToStackAfterX;
                float whatToStackAfterY;

                var whatToStackAfter = GetWhatToStackAfter(canWrap, shouldWrap, out whatToStackAfterX, out whatToStackAfterY);



                float xRelativeTo = 0;
                float yRelativeTo = 0;

                if (whatToStackAfter != null)
                {
                    var effectiveParent = this.EffectiveParentGue;
                    switch (effectiveParent.ChildrenLayout)
                    {
                        case Gum.Managers.ChildrenLayout.TopToBottomStack:

                            if (canWrap)
                            {
                                xRelativeTo = whatToStackAfterX;
                            }

                            yRelativeTo = whatToStackAfterY;

                            break;
                        case Gum.Managers.ChildrenLayout.LeftToRightStack:

                            xRelativeTo = whatToStackAfterX;

                            if (canWrap)
                            {
                                yRelativeTo = whatToStackAfterY;
                            }
                            break;
                        default:
                            throw new NotImplementedException();
                    }
                }

                unitOffsetX += xRelativeTo;
                unitOffsetY += yRelativeTo;
            }
            else if(GetIfParentIsAutoGrid())
            {
                var indexInSiblingList = this.GetIndexInSiblings();
                int xIndex, yIndex;
                float cellWidth, cellHeight;
                GetCellDimensions(indexInSiblingList, out xIndex, out yIndex, out cellWidth, out cellHeight);

                unitOffsetX += cellWidth * xIndex;
                unitOffsetY += cellHeight * yIndex;
            }
        }

        private void GetCellDimensions(int indexInSiblingList, out int xIndex, out int yIndex, out float cellWidth, out float cellHeight)
        {
            var effectiveParent = EffectiveParentGue;
            var xRows = effectiveParent.AutoGridHorizontalCells;
            var yRows = effectiveParent.AutoGridVerticalCells;
            if (xRows < 1) xRows = 1;
            if (yRows < 1) yRows = 1;

            if(effectiveParent.ChildrenLayout == ChildrenLayout.AutoGridHorizontal)
            {
                xIndex = indexInSiblingList % xRows;
                yIndex = indexInSiblingList / xRows;
            }
            else // vertical
            {
                yIndex = indexInSiblingList % yRows;
                xIndex = indexInSiblingList / yRows;
            }
            var parentWidth = effectiveParent.GetAbsoluteWidth();
            var parentHeight = effectiveParent.GetAbsoluteHeight();

            cellWidth = parentWidth / xRows;
            cellHeight = parentHeight / yRows;
        }

        private int GetIndexInSiblings()
        {
            System.Collections.IList siblings = null;

            if (this.Parent == null)
            {
                siblings = this.ElementGueContainingThis.mWhatThisContains;
            }
            else if (this.Parent is GraphicalUiElement)
            {
                siblings = ((GraphicalUiElement)Parent).Children as System.Collections.IList;
            }

            var thisIndex = 0;
            for(int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] == this)
                {
                    break;
                }
                if ((siblings[i] as IVisible).Visible)
                {
                    thisIndex++;
                }
            }

            return thisIndex;
        }

        private bool GetIfParentStacks()
        {
            return this.EffectiveParentGue != null &&
                (this.EffectiveParentGue.ChildrenLayout == ChildrenLayout.TopToBottomStack ||
                this.EffectiveParentGue.ChildrenLayout == ChildrenLayout.LeftToRightStack);
        }
                
        private bool GetIfParentIsAutoGrid()
        {             
            return this.EffectiveParentGue != null &&
                (this.EffectiveParentGue.ChildrenLayout == ChildrenLayout.AutoGridHorizontal ||
                this.EffectiveParentGue.ChildrenLayout == ChildrenLayout.AutoGridVertical);
        }

        private GraphicalUiElement GetWhatToStackAfter(bool canWrap, bool shouldWrap, out float whatToStackAfterX, out float whatToStackAfterY)
        {
            var parentGue = this.EffectiveParentGue;

            int thisIndex = 0;

            // We used to have a static list we were populating, but that allocates memory so we
            // now use the actual list.
            System.Collections.IList siblings = null;

            if (this.Parent == null)
            {
                siblings = this.ElementGueContainingThis.mWhatThisContains;
            }
            else if (this.Parent is GraphicalUiElement)
            {
                siblings = ((GraphicalUiElement)Parent).Children as System.Collections.IList;
            }
            thisIndex = siblings.IndexOf(this);

            IPositionedSizedObject whatToStackAfter = null;
            whatToStackAfterX = 0;
            whatToStackAfterY = 0;

            if (parentGue.StackedRowOrColumnDimensions == null)
            {
                parentGue.StackedRowOrColumnDimensions = new List<float>();
            }

            int thisRowOrColumnIndex = 0;



            if (thisIndex > 0)
            {
                var index = thisIndex - 1;
                while (index > -1)
                {
                    if ((siblings[index] as IVisible).Visible)
                    {
                        whatToStackAfter = siblings[index] as GraphicalUiElement;
                        break;
                    }
                    index--;
                }
            }

            if (whatToStackAfter != null)
            {
                if (shouldWrap)
                {
                    // This is going to be on a new row/column. That means the following are true:
                    // * It will have a previous sibling.
                    // * It will be positioned at the start/end of its row/column
                    this.StackedRowOrColumnIndex = (whatToStackAfter as GraphicalUiElement).StackedRowOrColumnIndex + 1;


                    thisRowOrColumnIndex = (whatToStackAfter as GraphicalUiElement).StackedRowOrColumnIndex + 1;
                    var previousRowOrColumnIndex = thisRowOrColumnIndex - 1;
                    if (parentGue.ChildrenLayout == Gum.Managers.ChildrenLayout.LeftToRightStack)
                    {
                        whatToStackAfterX = 0;

                        whatToStackAfterY = 0;
                        for (int i = 0; i < thisRowOrColumnIndex; i++)
                        {
                            whatToStackAfterY += parentGue.StackedRowOrColumnDimensions[i] + parentGue.StackSpacing;
                        }
                    }
                    else // top to bottom stack
                    {
                        whatToStackAfterY = 0;
                        whatToStackAfterX = 0;
                        for (int i = 0; i < thisRowOrColumnIndex; i++)
                        {
                            whatToStackAfterX += parentGue.StackedRowOrColumnDimensions[i] + parentGue.StackSpacing;
                        }
                    }

                }
                else
                {

                    if (whatToStackAfter != null)
                    {
                        thisRowOrColumnIndex = (whatToStackAfter as GraphicalUiElement).StackedRowOrColumnIndex;

                        this.StackedRowOrColumnIndex = (whatToStackAfter as GraphicalUiElement).StackedRowOrColumnIndex;
                        if (parentGue.ChildrenLayout == Gum.Managers.ChildrenLayout.LeftToRightStack)
                        {
                            whatToStackAfterX = whatToStackAfter.X + whatToStackAfter.Width + parentGue.StackSpacing;

                            whatToStackAfterY = 0;
                            for (int i = 0; i < thisRowOrColumnIndex; i++)
                            {
                                whatToStackAfterY += parentGue.StackedRowOrColumnDimensions[i] + parentGue.StackSpacing;
                            }
                        }
                        else
                        {
                            whatToStackAfterY = whatToStackAfter.Y + whatToStackAfter.Height + parentGue.StackSpacing;
                            whatToStackAfterX = 0;
                            for (int i = 0; i < thisRowOrColumnIndex; i++)
                            {
                                whatToStackAfterX += parentGue.StackedRowOrColumnDimensions[i] + parentGue.StackSpacing;
                            }
                        }

                        // This is on the same row/column as its previous sibling
                    }
                }
            }
            else
            {
                StackedRowOrColumnIndex = 0;
            }

            return whatToStackAfter as GraphicalUiElement;
        }

        private void AdjustOffsetsByOrigin(bool isParentFlippedHorizontally, ref float unitOffsetX, ref float unitOffsetY)
        {
#if DEBUG
            if (float.IsPositiveInfinity(mRotation) || float.IsNegativeInfinity(mRotation))
            {
                throw new Exception("Rotation cannot be negative/positive infinity");
            }
#endif
            float offsetX = 0;
            float offsetY = 0;

            HorizontalAlignment effectiveXorigin = isParentFlippedHorizontally ? mXOrigin.Flip() : mXOrigin;

            if (!float.IsNaN(mContainedObjectAsIpso.Width))
            {
                if (effectiveXorigin == HorizontalAlignment.Center)
                {
                    offsetX -= mContainedObjectAsIpso.Width / 2.0f;
                }
                else if (effectiveXorigin == HorizontalAlignment.Right)
                {
                    offsetX -= mContainedObjectAsIpso.Width;
                }
            }
            // no need to handle left


            if (mYOrigin == VerticalAlignment.Center)
            {
                offsetY -= mContainedObjectAsIpso.Height / 2.0f;
            }
            else if (mYOrigin == VerticalAlignment.TextBaseline)
            {
                if (mContainedObjectAsIpso is IText text)
                {
                    offsetY += -mContainedObjectAsIpso.Height + text.DescenderHeight * text.FontScale;
                }
                else
                {
                    offsetY -= mContainedObjectAsIpso.Height;
                }
            }
            else if (mYOrigin == VerticalAlignment.Bottom)
            {
                offsetY -= mContainedObjectAsIpso.Height;
            }
            // no need to handle top

            // Adjust offsets by rotation
            if (mRotation != 0)
            {
                var rotation = isParentFlippedHorizontally ? -mRotation : mRotation;

                GetRightAndUpFromRotation(rotation, out Vector3 right, out Vector3 up);

                var unrotatedX = offsetX;
                var unrotatedY = offsetY;

                offsetX = right.X * unrotatedX + up.X * unrotatedY;
                offsetY = right.Y * unrotatedX + up.Y * unrotatedY;
            }

            unitOffsetX += offsetX;
            unitOffsetY += offsetY;
        }

        static void GetRightAndUpFromRotation(float rotationInDegrees, out Vector3 right, out Vector3 up)
        {

            var quarterRotations = rotationInDegrees / 90;
            var radiansFromPerfectRotation = System.Math.Abs(quarterRotations - MathFunctions.RoundToInt(quarterRotations));

            const float errorToTolerate = .1f / 90f;

            if(radiansFromPerfectRotation < errorToTolerate)
            {
                var quarterRotationsAsInt = MathFunctions.RoundToInt(quarterRotations) % 4;
                if (quarterRotationsAsInt < 0)
                {
                    quarterRotationsAsInt += 4;
                }

                // invert it to match how rotation works with the CreateRotationZ method:
                quarterRotationsAsInt = 4 - quarterRotationsAsInt;

                right = Vector3Extensions.Right;
                up = Vector3Extensions.Up;

                switch (quarterRotationsAsInt)
                {
                    case 0:
                        right = Vector3Extensions.Right;
                        up = Vector3Extensions.Up;
                        break;
                    case 1:
                        right = Vector3Extensions.Up;
                        up = Vector3Extensions.Left;
                        break;
                    case 2:
                        right = Vector3Extensions.Left;
                        up = Vector3Extensions.Down;
                        break;

                    case 3:
                        right = Vector3Extensions.Down;
                        up = Vector3Extensions.Right;
                        break;
                }
            }
            else
            {
                var matrix = Matrix.CreateRotationZ(-MathHelper.ToRadians(rotationInDegrees));
                right = matrix.Right();
                up = matrix.Up();
            }

        }

        private void AdjustParentOriginOffsetsByUnits(float parentWidth, float parentHeight, bool isParentFlippedHorizontally,
            ref float unitOffsetX, ref float unitOffsetY, ref bool wasHandledX, ref bool wasHandledY)
        {

            var shouldAdd = Parent is GraphicalUiElement parentGue && 
                (parentGue.ChildrenLayout == Gum.Managers.ChildrenLayout.AutoGridVertical || parentGue.ChildrenLayout == Gum.Managers.ChildrenLayout.AutoGridHorizontal);

            if (!wasHandledX)
            {
                var units = isParentFlippedHorizontally ? mXUnits.Flip() : mXUnits;

                var value = 0f;
                if (units == GeneralUnitType.PixelsFromLarge)
                {
                    value = parentWidth;
                    wasHandledX = true;
                }
                else if (units == GeneralUnitType.PixelsFromMiddle)
                {
                    value = parentWidth / 2.0f;
                    wasHandledX = true;
                }
                else if (units == GeneralUnitType.PixelsFromSmall)
                {
                    // no need to do anything
                }

                if(shouldAdd)
                {
                    unitOffsetX += value;
                }
                else if(mXUnits != GeneralUnitType.PixelsFromSmall)
                {
                    unitOffsetX = value;
                }
            }

            if (!wasHandledY)
            {
                var value = 0f;
                if (mYUnits == GeneralUnitType.PixelsFromLarge)
                {
                    value = parentHeight;
                    wasHandledY = true;
                }
                else if (mYUnits == GeneralUnitType.PixelsFromMiddle || mYUnits == GeneralUnitType.PixelsFromMiddleInverted)
                {
                    value = parentHeight / 2.0f;
                    wasHandledY = true;
                }
                else if (mYUnits == GeneralUnitType.PixelsFromBaseline)
                {
                    if (Parent is GraphicalUiElement gue && gue.RenderableComponent is IText text)
                    {
                        value = parentHeight - text.DescenderHeight;
                    }
                    else
                    {
                        // use the bottom as baseline:
                        value = parentHeight;
                    }
                    wasHandledY = true;
                }

                if (shouldAdd)
                {
                    unitOffsetY += value;
                }
                else if(mYUnits != GeneralUnitType.PixelsFromSmall)
                {
                    unitOffsetY = value;
                }
            }
        }

        private void AdjustOffsetsByUnits(float parentWidth, float parentHeight, bool isParentFlippedHorizontally, XOrY? xOrY, ref float unitOffsetX, ref float unitOffsetY)
        {
            bool doX = xOrY == null || xOrY == XOrY.X;
            bool doY = xOrY == null || xOrY == XOrY.Y;

            if (doX)
            {
                if (mXUnits == GeneralUnitType.Percentage)
                {
                    unitOffsetX = parentWidth * mX / 100.0f;
                }
                else if (mXUnits == GeneralUnitType.PercentageOfFile)
                {
                    bool wasSet = false;

                    if (mContainedObjectAsIpso is ITextureCoordinate asITextureCoordinate)
                    {
                        if (asITextureCoordinate.TextureWidth != null)
                        {
                            unitOffsetX = asITextureCoordinate.TextureWidth.Value * mX / 100.0f;
                        }
                    }

                    if (!wasSet)
                    {
                        unitOffsetX = 64 * mX / 100.0f;
                    }
                }
                else
                {
                    if (isParentFlippedHorizontally)
                    {
                        unitOffsetX -= mX;
                    }
                    else
                    {
                        unitOffsetX += mX;
                    }
                }
            }

            if (doY)
            {
                if (mYUnits == GeneralUnitType.Percentage)
                {
                    unitOffsetY = parentHeight * mY / 100.0f;
                }
                else if (mYUnits == GeneralUnitType.PercentageOfFile)
                {

                    bool wasSet = false;


                    if (mContainedObjectAsIpso is ITextureCoordinate asITextureCoordinate)
                    {
                        if (asITextureCoordinate.TextureHeight != null)
                        {
                            unitOffsetY = asITextureCoordinate.TextureHeight.Value * mY / 100.0f;
                        }
                    }

                    if (!wasSet)
                    {
                        unitOffsetY = 64 * mY / 100.0f;
                    }
                }
                else if (mYUnits == GeneralUnitType.PixelsFromMiddleInverted)
                {
                    unitOffsetY += -mY;
                }
                else
                {
                    unitOffsetY += mY;
                }
            }
        }

        private void UpdateDimensions(float parentWidth, float parentHeight, XOrY? xOrY, bool considerWrappedStacked)
        {
            // special case - if the user has set both values to depend on the other value, we don't want to have an infinite recursion so we'll just apply the width and height values as pixel values.
            // This really doesn't make much sense but...the alternative would be an object that may grow or shrink infinitely, which may cause lots of other problems:
            if ((mWidthUnit == DimensionUnitType.PercentageOfOtherDimension && mHeightUnit == DimensionUnitType.PercentageOfOtherDimension) ||
                (mWidthUnit == DimensionUnitType.MaintainFileAspectRatio && mHeightUnit == DimensionUnitType.MaintainFileAspectRatio)
                )
            {
                mContainedObjectAsIpso.Width = mWidth;
                mContainedObjectAsIpso.Height = mHeight;
            }
            else
            {
                var doHeightFirst = mWidthUnit == DimensionUnitType.PercentageOfOtherDimension ||
                    mWidthUnit == DimensionUnitType.MaintainFileAspectRatio;

                // Explanation on why we use this:
                // Whenever an UpdateLayout happens,
                // the parent may tell its children to
                // update on only one axis. This allows
                // the child to update its absolute dimension
                // along that axis which the parent can then use
                // to update its own dimensions. However, if the axis
                // that the parent requested depends on the other axis on
                // the child, then the child will not be able to properly update
                // the requested axis until it updates the other axis. Therefore,
                // we should attempt to update both, but ONLY if the other axis is
                var widthUnitDependencyType = mWidthUnit.GetDependencyType();
                var heightUnitDependencyType = mHeightUnit.GetDependencyType();

                if (doHeightFirst)
                {
                    // if width depends on height, do height first:
                    if (xOrY == null || xOrY == XOrY.Y || heightUnitDependencyType == HierarchyDependencyType.NoDependency)
                    {
                        UpdateHeight(parentHeight, considerWrappedStacked);
                    }
                    if (xOrY == null || xOrY == XOrY.X || widthUnitDependencyType == HierarchyDependencyType.NoDependency)
                    {
                        UpdateWidth(parentWidth, considerWrappedStacked);
                    }
                }
                else // either width needs to be first, or it doesn't matter so we just do width first arbitrarily
                {
                    // If height depends on width, do width first
                    if (xOrY == null || xOrY == XOrY.X || widthUnitDependencyType == HierarchyDependencyType.NoDependency)
                    {
                        UpdateWidth(parentWidth, considerWrappedStacked);
                    }
                    if (xOrY == null || xOrY == XOrY.Y || heightUnitDependencyType == HierarchyDependencyType.NoDependency)
                    {
                        UpdateHeight(parentHeight, considerWrappedStacked);
                    }
                }
            }
        }

        public void UpdateHeight(float parentHeight, bool considerWrappedStacked)
        {
            float pixelHeightToSet = mHeight;


            #region AbsoluteMultipliedByFontScale

            if (mHeightUnit == DimensionUnitType.AbsoluteMultipliedByFontScale)
            {
                pixelHeightToSet *= GlobalFontScale;
            }

            #endregion

            #region ScreenPixel

            else if(mHeightUnit == DimensionUnitType.ScreenPixel)
            {
                var effectiveManagers = this.EffectiveManagers;
                if (effectiveManagers != null)
                {
                    pixelHeightToSet /= effectiveManagers.Renderer.Camera.Zoom;
                }
            }

            #endregion

            #region RelativeToChildren

            if (mHeightUnit == DimensionUnitType.RelativeToChildren)
            {
                float maxHeight = 0;


                if (this.mContainedObjectAsIpso != null)
                {
                    if (mContainedObjectAsIpso is IText asText)
                    {
                        var oldWidth = mContainedObjectAsIpso.Width;
                        if (WidthUnits == DimensionUnitType.RelativeToChildren)
                        {
                            mContainedObjectAsIpso.Width = float.PositiveInfinity;
                        }
                        maxHeight = asText.WrappedTextHeight;
                        mContainedObjectAsIpso.Width = oldWidth;
                    }

                    if(useFixedStackChildrenSize && this.ChildrenLayout == ChildrenLayout.TopToBottomStack && this.Children.Count > 1)
                    {
                        var element = Children[0] as GraphicalUiElement;

                        maxHeight = element.GetRequiredParentHeight();
                        var elementHeight = element.GetAbsoluteHeight();
                        maxHeight += (StackSpacing + elementHeight) * (Children.Count - 1);
                    }
                    else
                    {
                        for(int i = 0; i < Children.Count; i++)
                        {
                            var element = Children[i] as GraphicalUiElement;
                            var childLayout = element.GetChildLayoutType(XOrY.Y, this);
                            var considerChild = (childLayout == ChildType.Absolute || (considerWrappedStacked && childLayout == ChildType.StackedWrapped)) && element.IgnoredByParentSize == false;
                            if (considerChild && element.Visible)
                            {
                                var elementHeight = element.GetRequiredParentHeight();

                                if (this.ChildrenLayout == ChildrenLayout.TopToBottomStack)
                                {
                                    // The first item in the stack doesn't consider the stack spacing, but all subsequent ones do:
                                    if(i != 0)
                                    {
                                        maxHeight += StackSpacing;
                                    }
                                    maxHeight += elementHeight;
                                }
                                else
                                {
                                    maxHeight = System.Math.Max(maxHeight, elementHeight);
                                }
                            }
                        }

                    }
                }
                else
                {
                    for(int i = 0; i < mWhatThisContains.Count; i++)
                    {
                        var element = mWhatThisContains[i];
                        var childLayout = element.GetChildLayoutType(XOrY.Y, this);
                        var considerChild = (childLayout == ChildType.Absolute || (considerWrappedStacked && childLayout == ChildType.StackedWrapped)) && element.IgnoredByParentSize == false;

                        if (considerChild && element.Visible)
                        {
                            var elementHeight = element.GetRequiredParentHeight();
                            if (this.ChildrenLayout == ChildrenLayout.TopToBottomStack)
                            {
                                // The first item in the stack doesn't consider the stack spacing, but all subsequent ones do:
                                if (i != 0)
                                {
                                    maxHeight += StackSpacing;
                                }
                                maxHeight += elementHeight;
                            }
                            else
                            {
                                maxHeight = System.Math.Max(maxHeight, elementHeight);
                            }
                        }
                    }
                }

                pixelHeightToSet = maxHeight + mHeight;
            }

            #endregion

            #region Percentage (of parent)

            else if (mHeightUnit == DimensionUnitType.Percentage)
            {
                pixelHeightToSet = parentHeight * mHeight / 100.0f;
            }

            #endregion

            #region PercentageOfSourceFile

            else if (mHeightUnit == DimensionUnitType.PercentageOfSourceFile)
            {
                bool wasSet = false;

                if (mTextureHeight > 0)
                {
                    pixelHeightToSet = mTextureHeight * mHeight / 100.0f;
                    wasSet = true;
                }

                if (mContainedObjectAsIpso is ITextureCoordinate iTextureCoordinate)
                {
                    if (iTextureCoordinate.TextureHeight != null)
                    {
                        pixelHeightToSet = iTextureCoordinate.TextureHeight.Value * mHeight / 100.0f;
                        wasSet = true;
                    }

                    // If the address is dimension based, then that means texture coords depend on dimension...but we
                    // can't make dimension based on texture coords as that would cause a circular reference
                    if (iTextureCoordinate.SourceRectangle.HasValue && mTextureAddress != TextureAddress.DimensionsBased)
                    {
                        pixelHeightToSet = iTextureCoordinate.SourceRectangle.Value.Height * mHeight / 100.0f;
                        wasSet = true;
                    }
                }

                if (!wasSet)
                {
                    pixelHeightToSet = 64 * mHeight / 100.0f;
                }
            }

            #endregion

            #region MaintainFileAspectRatio

            else if (mHeightUnit == DimensionUnitType.MaintainFileAspectRatio)
            {
                bool wasSet = false;


                if (mContainedObjectAsIpso is IAspectRatio aspectRatioObject)
                {
                    //if(sprite.AtlasedTexture != null)
                    //{
                    //    throw new NotImplementedException();
                    //}
                    //else 
                    pixelHeightToSet = GetAbsoluteWidth() * (mHeight / 100.0f) / aspectRatioObject.AspectRatio;
                    wasSet = true;

                    if (wasSet && mContainedObjectAsIpso is ITextureCoordinate textureCoordinate)
                    {
                        // If the address is dimension based, then that means texture coords depend on dimension...but we
                        // can't make dimension based on texture coords as that would cause a circular reference
                        if (textureCoordinate.SourceRectangle.HasValue && mTextureAddress != TextureAddress.DimensionsBased)
                        {
                            var scale = GetAbsoluteWidth() / textureCoordinate.SourceRectangle.Value.Width;
                            pixelHeightToSet = textureCoordinate.SourceRectangle.Value.Height * scale * mHeight / 100.0f;
                        }
                    }
                }
                if (!wasSet)
                {
                    pixelHeightToSet = 64 * mHeight / 100.0f;
                }
            }

            #endregion

            #region RelativeToContainer (in pixels)

            else if (mHeightUnit == DimensionUnitType.RelativeToContainer)
            {
                pixelHeightToSet = parentHeight + mHeight;
            }

            #endregion

            #region PercentageOfOtherDimension

            else if (mHeightUnit == DimensionUnitType.PercentageOfOtherDimension)
            {
                pixelHeightToSet = mContainedObjectAsIpso.Width * mHeight / 100.0f;
            }

            #endregion

            #region Ratio
            else if (mHeightUnit == DimensionUnitType.Ratio)
            {
                if(this.Height == 0)
                {
                    pixelHeightToSet = 0;
                }
                else
                {
                    var heightToSplit = parentHeight;

                    var numberOfVisibleChildren = 0;

                    if (mParent != null)
                    {
                        for(int i = 0; i < mParent.Children.Count; i++)
                        {
                            var child = mParent.Children[i];
                            if (child != this && child is GraphicalUiElement gue)
                            {
                                if (gue.HeightUnits == DimensionUnitType.Absolute || gue.HeightUnits == DimensionUnitType.AbsoluteMultipliedByFontScale)
                                {
                                    heightToSplit -= gue.Height;
                                }
                                else if (gue.HeightUnits == DimensionUnitType.RelativeToContainer)
                                {
                                    var childAbsoluteWidth = parentHeight - gue.Height;
                                    heightToSplit -= childAbsoluteWidth;
                                }
                                else if (gue.HeightUnits == DimensionUnitType.Percentage)
                                {
                                    var childAbsoluteWidth = parentHeight * gue.Height;
                                    heightToSplit -= childAbsoluteWidth;
                                }
                                // this depends on the sibling being updated before this:
                                else if (gue.HeightUnits == DimensionUnitType.RelativeToChildren)
                                {
                                    var childAbsoluteWidth = gue.GetAbsoluteHeight();
                                    heightToSplit -= childAbsoluteWidth;
                                }
                                if (gue.Visible)
                                {
                                    numberOfVisibleChildren++;
                                }
                            }
                        }
                    }

                    if(mParent is GraphicalUiElement parentGue && parentGue.ChildrenLayout == ChildrenLayout.TopToBottomStack && parentGue.StackSpacing != 0)
                    {
                        var numberOfSpaces = numberOfVisibleChildren;
                        heightToSplit -= numberOfSpaces * parentGue.StackSpacing;
                    }

                    float totalRatio = 0;
                    if (mParent != null)
                    {
                        for(int i = 0; i < mParent.Children.Count; i++)
                        {
                            var child = mParent.Children[i];
                            if (child is GraphicalUiElement gue && gue.HeightUnits == DimensionUnitType.Ratio && gue.Visible)
                            {
                                totalRatio += gue.Height;
                            }
                        }
                    }
                    if (totalRatio > 0)
                    {
                        pixelHeightToSet = heightToSplit * (this.Height / totalRatio);
                    }
                    else
                    {
                        pixelHeightToSet = heightToSplit;
                    }
                }
            }
            #endregion

            mContainedObjectAsIpso.Height = pixelHeightToSet;
        }

        public void UpdateWidth(float parentWidth, bool considerWrappedStacked)
        {
            float widthToSet = mWidth;

            #region AbsoluteMultipliedByFontScale

            if (mWidthUnit == DimensionUnitType.AbsoluteMultipliedByFontScale)
            {
                widthToSet *= GlobalFontScale;
            }

            #endregion

            #region ScreenPixel

            else if (mWidthUnit == DimensionUnitType.ScreenPixel)
            {
                var effectiveManagers = this.EffectiveManagers;
                if (effectiveManagers != null)
                {
                    widthToSet /= effectiveManagers.Renderer.Camera.Zoom;
                }
            }

            #endregion

            #region RelativeToChildren

            else if (mWidthUnit == DimensionUnitType.RelativeToChildren)
            {
                float maxWidth = 0;

                List<GraphicalUiElement> childrenToUse = mWhatThisContains;

                if (this.mContainedObjectAsIpso != null)
                {
                    if (mContainedObjectAsIpso is IText asText)
                    {

                        // Sometimes this crashes in Skia.
                        // Not sure why, but I think it is some kind of internal error. We can tolerate it instead of blow up:
                        try
                        {
                            // It's possible that the text has itself wrapped, but the dimensions changed.
                            if (
                                // Skia text doesn't have a wrapped text, but we can just check if the text itself is not null or empty
                                //asText.WrappedText.Count > 0 &&
                                !string.IsNullOrEmpty(asText.RawText) &&
                                (mContainedObjectAsIpso.Width != 0 && float.IsPositiveInfinity(mContainedObjectAsIpso.Width) == false))
                            {
                                // this could be either because it wrapped, or because the raw text
                                // actually has newlines. Vic says - this difference could maybe be tested
                                // but I'm not sure it's worth the extra code for the minor savings here, so just
                                // set the wrap width to positive infinity and refresh the text
                                mContainedObjectAsIpso.Width = float.PositiveInfinity;
                            }

                            maxWidth = asText.WrappedTextWidth;
                        }
                        catch (BadImageFormatException)
                        {
                            // not sure why but let's tolerate:
                            // https://appcenter.ms/orgs/Mtn-Green-Engineering/apps/BioCheck-2/crashes/errors/738313670/overview
                            maxWidth = 64;

                            //        // It's possible that the text has itself wrapped, but the dimensions changed.
                            //        if (asText.WrappedText.Count > 0 &&
                            //            (asText.Width != 0 && float.IsPositiveInfinity(asText.Width) == false))
                            //        {
                            //            // this could be either because it wrapped, or because the raw text
                            //            // actually has newlines. Vic says - this difference could maybe be tested
                            //            // but I'm not sure it's worth the extra code for the minor savings here, so just
                            //            // set the wrap width to positive infinity and refresh the text
                            //            asText.Width = float.PositiveInfinity;
                            //        }

                            //        maxWidth = asText.WrappedTextWidth;
                        }
                    }

                    for(int i = 0; i < this.Children.Count; i++)
                    {
                        var element = this.Children[i] as GraphicalUiElement;
                        var childLayout = element.GetChildLayoutType(XOrY.X, this);
                        var considerChild = (childLayout == ChildType.Absolute || (considerWrappedStacked && childLayout == ChildType.StackedWrapped)) && element.IgnoredByParentSize == false;

                        if (considerChild && element.Visible)
                        {
                            var elementWidth = element.GetRequiredParentWidth();

                            if (this.ChildrenLayout == ChildrenLayout.LeftToRightStack)
                            {
                                // The first item in the stack doesn't consider the stack spacing, but all subsequent ones do:
                                if (i != 0)
                                {
                                    maxWidth += StackSpacing;
                                }
                                maxWidth += elementWidth;
                            }
                            else
                            {
                                maxWidth = System.Math.Max(maxWidth, elementWidth);
                            }
                        }
                    }
                }
                else
                {
                    for(int i = 0; i < mWhatThisContains.Count; i++)
                    {
                        var element = mWhatThisContains[i];
                        var childLayout = element.GetChildLayoutType(XOrY.X, this);
                        var considerChild = (childLayout == ChildType.Absolute || (considerWrappedStacked && childLayout == ChildType.StackedWrapped)) && element.IgnoredByParentSize == false;

                        if (considerChild && element.Visible)
                        {
                            var elementWidth = element.GetRequiredParentWidth();

                            if (this.ChildrenLayout == ChildrenLayout.LeftToRightStack)
                            {
                                // The first item in the stack doesn't consider the stack spacing, but all subsequent ones do:
                                if (i != 0)
                                {
                                    maxWidth += StackSpacing;
                                }
                                maxWidth += elementWidth;
                            }
                            else
                            {
                                maxWidth = System.Math.Max(maxWidth, elementWidth);
                            }
                        }
                    }
                }

                widthToSet = maxWidth + mWidth;
            }
            #endregion

            #region Percentage (of parent)

            else if (mWidthUnit == DimensionUnitType.Percentage)
            {
                widthToSet = parentWidth * mWidth / 100.0f;
            }

            #endregion

            #region PercentageOfSourceFile

            else if (mWidthUnit == DimensionUnitType.PercentageOfSourceFile)
            {
                bool wasSet = false;

                if (mTextureWidth > 0)
                {
                    widthToSet = mTextureWidth * mWidth / 100.0f;
                    wasSet = true;
                }

                if (mContainedObjectAsIpso is ITextureCoordinate iTextureCoordinate)
                {
                    var width = iTextureCoordinate.TextureWidth;
                    if (width != null)
                    {
                        widthToSet = width.Value * mWidth / 100.0f;
                        wasSet = true;
                    }

                    // If the address is dimension based, then that means texture coords depend on dimension...but we
                    // can't make dimension based on texture coords as that would cause a circular reference
                    if (iTextureCoordinate.SourceRectangle.HasValue && mTextureAddress != TextureAddress.DimensionsBased)
                    {
                        widthToSet = iTextureCoordinate.SourceRectangle.Value.Width * mWidth / 100.0f;
                        wasSet = true;
                    }
                }

                if (!wasSet)
                {
                    widthToSet = 64 * mWidth / 100.0f;
                }
            }

            #endregion

            #region MaintainFileAspectRatio

            else if (mWidthUnit == DimensionUnitType.MaintainFileAspectRatio)
            {
                bool wasSet = false;

                if (mContainedObjectAsIpso is IAspectRatio aspectRatioObject)
                {
                    // mWidth is a percent where 100 means maintain aspect ratio
                    widthToSet = GetAbsoluteHeight() * aspectRatioObject.AspectRatio * (mWidth / 100.0f);
                    wasSet = true;

                    if (wasSet && mContainedObjectAsIpso is ITextureCoordinate iTextureCoordinate)
                    {
                        if (iTextureCoordinate.SourceRectangle.HasValue && mTextureAddress != TextureAddress.DimensionsBased)
                        {
                            var scale = GetAbsoluteHeight() / iTextureCoordinate.SourceRectangle.Value.Height;
                            widthToSet = iTextureCoordinate.SourceRectangle.Value.Width * scale * mWidth / 100.0f;
                        }
                    }
                }
                if (!wasSet)
                {
                    widthToSet = 64 * mWidth / 100.0f;
                }
            }

            #endregion

            #region RelativeToContainer (in pixels)

            else if (mWidthUnit == DimensionUnitType.RelativeToContainer)
            {
                widthToSet = parentWidth + mWidth;
            }

            #endregion

            #region PercentageOfOtherDimension

            else if (mWidthUnit == DimensionUnitType.PercentageOfOtherDimension)
            {
                widthToSet = mContainedObjectAsIpso.Height * mWidth / 100.0f;
            }

            #endregion

            #region Ratio

            else if (mWidthUnit == DimensionUnitType.Ratio)
            {
                if(this.Width == 0)
                {
                    widthToSet = 0;
                }
                else
                {
                    var widthToSplit = parentWidth;

                    var numberOfVisibleChildren = 0;

                    if (mParent != null)
                    {
                        for(int i = 0; i < mParent.Children.Count; i++)
                        {
                            var child = mParent.Children[i];
                            if (child != this && child is GraphicalUiElement gue)
                            {
                                if (gue.WidthUnits == DimensionUnitType.Absolute || gue.WidthUnits == DimensionUnitType.AbsoluteMultipliedByFontScale)
                                {
                                    widthToSplit -= gue.Width;
                                }
                                else if (gue.WidthUnits == DimensionUnitType.RelativeToContainer)
                                {
                                    var childAbsoluteWidth = parentWidth - gue.Width;
                                    widthToSplit -= childAbsoluteWidth;
                                }
                                else if (gue.WidthUnits == DimensionUnitType.Percentage)
                                {
                                    var childAbsoluteWidth = parentWidth * gue.Width;
                                    widthToSplit -= childAbsoluteWidth;
                                }
                                // this depends on the sibling being updated before this:
                                else if(gue.WidthUnits == DimensionUnitType.RelativeToChildren)
                                {
                                    var childAbsoluteWidth = gue.GetAbsoluteWidth();
                                    widthToSplit -= childAbsoluteWidth;
                                }

                                if(gue.Visible)
                                {
                                    numberOfVisibleChildren++;
                                }
                            }
                        }
                    }

                    if (mParent is GraphicalUiElement parentGue && parentGue.ChildrenLayout == ChildrenLayout.LeftToRightStack && parentGue.StackSpacing != 0)
                    {
                        var numberOfSpaces = numberOfVisibleChildren;

                        widthToSplit -= numberOfSpaces * parentGue.StackSpacing;
                    }

                    float totalRatio = 0;
                    if (mParent != null)
                    {
                        for(int i = 0; i < mParent.Children.Count; i++)
                        {
                            var child = mParent.Children[i];
                            if (child is GraphicalUiElement gue && gue.WidthUnits == DimensionUnitType.Ratio && gue.Visible)
                            {
                                totalRatio += gue.Width;
                            }
                        }
                    }
                    if (totalRatio > 0)
                    {
                        widthToSet = widthToSplit * (this.Width / totalRatio);

                    }
                    else
                    {
                        widthToSet = widthToSplit;
                    }
                }
            }

            #endregion

            mContainedObjectAsIpso.Width = widthToSet;
        }

        public override string ToString()
        {
            return Name;
        }

        public void SetGueValues(IVariableFinder rvf)
        {

            this.SuspendLayout();

            this.Width = rvf.GetValue<float>("Width");
            this.Height = rvf.GetValue<float>("Height");

            this.HeightUnits = rvf.GetValue<DimensionUnitType>("Height Units");
            this.WidthUnits = rvf.GetValue<DimensionUnitType>("Width Units");

            this.XOrigin = rvf.GetValue<HorizontalAlignment>("X Origin");
            this.YOrigin = rvf.GetValue<VerticalAlignment>("Y Origin");

            this.X = rvf.GetValue<float>("X");
            this.Y = rvf.GetValue<float>("Y");

            this.XUnits = UnitConverter.ConvertToGeneralUnit(rvf.GetValue<PositionUnitType>("X Units"));
            this.YUnits = UnitConverter.ConvertToGeneralUnit(rvf.GetValue<PositionUnitType>("Y Units"));

            this.TextureWidth = rvf.GetValue<int>("Texture Width");
            this.TextureHeight = rvf.GetValue<int>("Texture Height");
            this.TextureLeft = rvf.GetValue<int>("Texture Left");
            this.TextureTop = rvf.GetValue<int>("Texture Top");

            this.TextureWidthScale = rvf.GetValue<float>("Texture Width Scale");
            this.TextureHeightScale = rvf.GetValue<float>("Texture Height Scale");

            this.Wrap = rvf.GetValue<bool>("Wrap");

            this.TextureAddress = rvf.GetValue<TextureAddress>("Texture Address");

            this.ChildrenLayout = rvf.GetValue<ChildrenLayout>("Children Layout");
            this.WrapsChildren = rvf.GetValue<bool>("Wraps Children");
            this.ClipsChildren = rvf.GetValue<bool>("Clips Children");

            if (this.ElementSave != null)
            {
                foreach (var category in ElementSave.Categories)
                {
                    string valueOnThisState = rvf.GetValue<string>(category.Name + "State");

                    if (!string.IsNullOrEmpty(valueOnThisState))
                    {
                        this.ApplyState(valueOnThisState);
                    }
                }
            }

            this.ResumeLayout();
        }

        partial void CustomAddToManagers();

        /// <summary>
        /// Adds this as a renderable to the SystemManagers if not already added. If already added
        /// this does not perform any operations - it can be safely called multiple times.
        /// </summary>
        //public virtual void AddToManagers()
        //{

        //    AddToManagers(ISystemManagers.Default, null);

        //}

        /// <summary>
        /// Adds this as a renderable to the SystemManagers on the argument layer if not already added
        /// to SystemManagers. If already added
        /// this does not perform any operations - it can be safely called multiple times, but
        /// calling it multiple times will not move this to a different layer.
        /// </summary>
        public virtual void AddToManagers(ISystemManagers managers, Layer layer)
        {
#if DEBUG
            if (managers == null)
            {
                throw new ArgumentNullException("managers cannot be null");
            }
#endif
            // If mManagers isn't null, it's already been added
            if (mManagers == null)
            {
                mLayer = layer;
                mManagers = managers;

                AddContainedRenderableToManagers(managers, layer);

                RecursivelyAddIManagedChildren(this);

                // Custom should be called before children have their Custom called
                CustomAddToManagers();

                // that means this is a screen, so the children need to be added directly to managers
                if (this.mContainedObjectAsIpso == null)
                {
                    AddChildren(managers, layer);
                }
                else
                {
                    CustomAddChildren();
                }
            }
        }

        private static void RecursivelyAddIManagedChildren(GraphicalUiElement gue)
        {
            if (gue.ElementSave != null && gue.ElementSave is ScreenSave)
            {

                //Recursively add children to the managers
                foreach (var child in gue.mWhatThisContains)
                {
                    if(child is IManagedObject managedObject)
                    {
                        managedObject.AddToManagers();
                    }
                    RecursivelyAddIManagedChildren(child);
                }
            }
            else if (gue.Children != null)
            {
                foreach (var child in gue.Children)
                {
                    if (child is IManagedObject managedObject)
                    {
                        managedObject.AddToManagers();
                    }
                    if(child is GraphicalUiElement childGue)
                    {
                        RecursivelyAddIManagedChildren(childGue);
                    }
                }
            }
        }

        private void CustomAddChildren()
        {
            foreach (var child in this.mWhatThisContains)
            {
                child.mManagers = this.mManagers;
                child.CustomAddToManagers();

                child.CustomAddChildren();
            }
        }

        private void HandleCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                foreach (IRenderableIpso ipso in e.NewItems)
                {
                    if (ipso.Parent != this)
                    {
                        ipso.Parent = this;

                    }
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                foreach (IRenderableIpso ipso in e.OldItems)
                {
                    if (ipso.Parent == this)
                    {
                        ipso.Parent = null;
                    }
                }
            }
            else if(e.Action == NotifyCollectionChangedAction.Replace)
            {
                foreach (IRenderableIpso ipso in e.OldItems)
                {
                    if (ipso.Parent == this)
                    {
                        ipso.Parent = null;
                    }
                }
                foreach (IRenderableIpso ipso in e.NewItems)
                {
                    if (ipso.Parent != this)
                    {
                        ipso.Parent = this;

                    }
                }
            }
        }

        private void AddChildren(ISystemManagers managers, Layer layer)
        {
            // In a simple situation we'd just loop through the
            // ContainedElements and add them to the manager.  However,
            // this means that the container will dictate the Layer that
            // its children reside on.  This is not what we want if we have
            // two children, one of which is attached to the other, and the parent
            // instance clips its children.  Therefore, we should make sure that we're
            // only adding direct children and letting instances handle their own children

            if (this.ElementSave != null && this.ElementSave is ScreenSave)
            {

                //Recursively add children to the managers
                foreach (var child in this.mWhatThisContains)
                {
                    // July 27, 2014
                    // Is this an unnecessary check?
                    // if (child is GraphicalUiElement)
                    {
                        // December 1, 2014
                        // I think that when we
                        // add a screen we should
                        // add all of the children of
                        // the screen.  There's nothing
                        // "above" that.
                        if (child.Parent == null || child.Parent == this)
                        {
                            (child as GraphicalUiElement).AddToManagers(managers, layer);
                        }
                        else
                        {
                            child.mManagers = this.mManagers;

                            child.CustomAddToManagers();

                            child.CustomAddChildren();
                        }
                    }
                }
            }
            else if (this.Children != null)
            {
                foreach (var child in this.Children)
                {
                    if (child is GraphicalUiElement)
                    {
                        var childGue = child as GraphicalUiElement;

                        if (child.Parent == null || child.Parent == this)
                        {
                            childGue.AddToManagers(managers, layer);
                        }
                        else
                        {
                            childGue.mManagers = this.mManagers;

                            childGue.CustomAddToManagers();

                            childGue.CustomAddChildren();
                        }
                    }
                }

                // If a Component contains a child and that child is parented to the screen bounds then we should still add it
                foreach (var child in this.mWhatThisContains)
                {
                    var childGue = child as GraphicalUiElement;

                    // We'll check if this child has a parent, and if that parent isn't part of this component. If not, then
                    // we'll add it
                    if (child.Parent != null && this.mWhatThisContains.Contains(child.Parent) == false)
                    {
                        childGue.AddToManagers(managers, layer);
                    }
                    else
                    {
                        childGue.mManagers = this.mManagers;

                        childGue.CustomAddToManagers();

                        childGue.CustomAddChildren();
                    }
                }
            }
        }


        private void AddContainedRenderableToManagers(ISystemManagers managers, Layer layer)
        {
            // This may be a Screen
            if (mContainedObjectAsIpso != null)
            {
                AddRenderableToManagers?.Invoke(mContainedObjectAsIpso, Managers, layer);

            }
        }

        // todo:  This should be called on instances and not just on element saves.  This is messing up animation
        public void AddExposedVariable(string variableName, string underlyingVariable)
        {
            mExposedVariables[variableName] = underlyingVariable;
        }

        public bool IsExposedVariable(string variableName)
        {
            return this.mExposedVariables.ContainsKey(variableName);
        }

        partial void CustomRemoveFromManagers();

        public void MoveToLayer(Layer layer)
        {
            var layerToRemoveFrom = mLayer;
            if (mLayer == null && mManagers != null)
            {
                layerToRemoveFrom = mManagers.Renderer.Layers[0];
            }

            var layerToAddTo = layer;
            if (layerToAddTo == null)
            {
                layerToAddTo = mManagers.Renderer.Layers[0];
            }

            bool isScreen = mContainedObjectAsIpso == null;
            if (!isScreen)
            {
                if (layerToRemoveFrom != null)
                {
                    layerToRemoveFrom.Remove(mContainedObjectAsIpso);
                }
                layerToAddTo.Add(mContainedObjectAsIpso);
            }
            else
            {
                // move all contained objects:
                foreach (var containedInstance in this.ContainedElements)
                {
                    var containedAsGue = containedInstance as GraphicalUiElement;
                    // If it's got a parent, the parent will handle it
                    if (containedAsGue.Parent == null)
                    {
                        containedAsGue.MoveToLayer(layer);
                    }
                }

            }
        }

        public void RemoveFromManagers()
        {
            foreach (var child in this.mWhatThisContains)
            {
                if (child is GraphicalUiElement)
                {
                    (child as GraphicalUiElement).RemoveFromManagers();
                }
            }

            // if mManagers is null, then it was never added to the managers
            if (mManagers != null)
            {
                RemoveRenderableFromManagers?.Invoke(mContainedObjectAsIpso, mManagers);

                CustomRemoveFromManagers();

                mManagers = null;
            }
        }

        public void SuspendLayout(bool recursive = false)
        {
            mIsLayoutSuspended = true;

            if (recursive)
            {
                if (this.Children?.Count > 0)
                {
                    var count = Children.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var asGraphicalUiElement = Children[i] as GraphicalUiElement;
                        asGraphicalUiElement?.SuspendLayout(true);
                    }
                }
                else
                {
                    for (int i = mWhatThisContains.Count - 1; i > -1; i--)
                    {
                        mWhatThisContains[i].SuspendLayout(true);
                    }

                }
            }
        }

        public void ResumeLayout(bool recursive = false)
        {
            mIsLayoutSuspended = false;

            if (recursive)
            {
                if(!IsAllLayoutSuspended)
                {
                    ResumeLayoutUpdateIfDirtyRecursive();
                }
            }
            else
            {
                if (isFontDirty)
                {
                    if (!IsAllLayoutSuspended)
                    {
                        this.UpdateToFontValues();
                        isFontDirty = false;
                    }
                }
                if (currentDirtyState != null)
                {
                    UpdateLayout(currentDirtyState.ParentUpdateType,
                        currentDirtyState.ChildrenUpdateDepth,
                        currentDirtyState.XOrY);
                }
            }
        }

        private bool ResumeLayoutUpdateIfDirtyRecursive()
        {

            mIsLayoutSuspended = false;
            UpdateFontRecursive();

            var didCallUpdateLayout = false;

            if (currentDirtyState != null)
            {
                didCallUpdateLayout = true;
                UpdateLayout(currentDirtyState.ParentUpdateType,
                    currentDirtyState.ChildrenUpdateDepth,
                    currentDirtyState.XOrY);
            }

            if(this.Children?.Count > 0)
            {
                var count = Children.Count;
                for (int i = 0; i < count; i++)
                {
                    var asGraphicalUiElement = Children[i] as GraphicalUiElement;
                    asGraphicalUiElement.ResumeLayoutUpdateIfDirtyRecursive();
                }
            }
            else
            {
                int count = mWhatThisContains.Count;
                for (int i = 0; i < count; i++)
                {
                    mWhatThisContains[i].ResumeLayoutUpdateIfDirtyRecursive();
                }
            }

            return didCallUpdateLayout;
        }

        /// <summary>
        /// Searches for and returns a GraphicalUiElement in this instance by name. Returns null
        /// if not found.
        /// </summary>
        /// <param name="name">The case-sensitive name to search for.</param>
        /// <returns>The found GraphicalUiElement, or null if no match is found.</returns>
        public GraphicalUiElement GetGraphicalUiElementByName(string name)
        {
            var containsDots = ToolsUtilities.StringFunctions.ContainsNoAlloc(name, '.');
            if (containsDots)
            {
                // rare, so we can do allocation calls here:
                var indexOfDot = name.IndexOf('.');

                var prefix = name.Substring(0, indexOfDot);

                GraphicalUiElement container = null;
                for (int i = mWhatThisContains.Count - 1; i > -1; i--)
                {
                    var item = mWhatThisContains[i];
                    if (item.name == prefix)
                    {
                        container = item;
                        break;
                    }
                }

                var suffix = name.Substring(indexOfDot + 1);

                return container?.GetGraphicalUiElementByName(suffix);
            }
            else
            {
                if (this.Children?.Count > 0 && mWhatThisContains.Count == 0)
                {
                    // This is a regular item that hasn't had its mWhatThisContains populated
                    return this.GetChildByNameRecursively(name) as GraphicalUiElement;
                }
                else
                {
                    for (int i = mWhatThisContains.Count - 1; i > -1; i--)
                    {
                        var item = mWhatThisContains[i];
                        if (item.name == name)
                        {
                            return item;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Performs a recursive search for graphical UI elements, where eacn name in the parameters
        /// is the name of a GraphicalUiElement one level deeper than the last.
        /// </summary>
        /// <param name="names">The names to search for, allowing retrieval multiple levels deep.</param>
        /// <returns>The found element, or null if no match is found.</returns>
        public GraphicalUiElement GetGraphicalUiElementByName(params string[] names)
        {
            if (names.Length > 0)
            {
                var directChild = GetGraphicalUiElementByName(names[0]);

                if (names.Length == 1)
                {
                    return directChild;
                }
                else
                {
                    var subArray = names.Skip(1).ToArray();

                    return directChild?.GetGraphicalUiElementByName(subArray);
                }
            }
            return null;
        }

        public IPositionedSizedObject GetChildByName(string name)
        {
            for(int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child.Name == name)
                {
                    return child;
                }
            }
            return null;
        }

        public IRenderableIpso GetChildByNameRecursively(string name)
        {
            return GetChildByName(Children, name);
        }

        private IRenderableIpso GetChildByName(ObservableCollection<IRenderableIpso> children, string name)
        {
            foreach (var child in children)
            {
                if (child.Name == name)
                {
                    return child;
                }

                var subChild = GetChildByName(child.Children, name);
                if (subChild != null)
                {
                    return subChild;
                }
            }
            return null;
        }


        static void SetPropertyThroughReflection(IRenderableIpso mContainedObjectAsIpso, GraphicalUiElement graphicalUiElement, string propertyName, object value)
        {
            System.Reflection.PropertyInfo propertyInfo = mContainedObjectAsIpso.GetType().GetProperty(propertyName);

            if (propertyInfo != null && propertyInfo.CanWrite)
            {

                if (value.GetType() != propertyInfo.PropertyType)
                {
                    value = System.Convert.ChangeType(value, propertyInfo.PropertyType);
                }
                propertyInfo.SetValue(mContainedObjectAsIpso, value, null);
            }
        }

        /// <summary>
        /// Sets a variable on this object (such as "X") to the argument value
        /// (such as 100.0f). This can be a primitive property like Height, or it can be
        /// a state.
        /// </summary>
        /// <param name="propertyName">The name of the variable on this object such as X or Height. If the property is a state, then the name should be "{CategoryName}State".</param>
        /// <param name="value">The value, casted to the correct type.</param>
        public void SetProperty(string propertyName, object value)
        {

            if (mExposedVariables.ContainsKey(propertyName))
            {
                string underlyingProperty = mExposedVariables[propertyName];
                int indexOfDot = underlyingProperty.IndexOf('.');
                string instanceName = underlyingProperty.Substring(0, indexOfDot);
                GraphicalUiElement containedGue = GetGraphicalUiElementByName(instanceName);
                string variable = underlyingProperty.Substring(indexOfDot + 1);

                // Children may not have been created yet
                if (containedGue != null)
                {
                    containedGue.SetProperty(variable, value);
                }
            }
            else if (ToolsUtilities.StringFunctions.ContainsNoAlloc(propertyName, '.'))
            {
                int indexOfDot = propertyName.IndexOf('.');
                string instanceName = propertyName.Substring(0, indexOfDot);
                GraphicalUiElement containedGue = GetGraphicalUiElementByName(instanceName);
                string variable = propertyName.Substring(indexOfDot + 1);

                // instances may not have been set yet
                if (containedGue != null)
                {
                    containedGue.SetProperty(variable, value);
                }


            }
            else if (TrySetValueOnThis(propertyName, value))
            {
                // success, do nothing, but it's in an else if to prevent the following else if's from evaluating
            }
            else if (this.mContainedObjectAsIpso != null)
            {
#if DEBUG
                if(SetPropertyOnRenderable == null)
                {
                    throw new Exception($"{nameof(SetPropertyOnRenderable)} must be set on GraphicalUiElement");
                }
#endif
                SetPropertyOnRenderable(mContainedObjectAsIpso, this, propertyName, value);
            }
        }

        private bool TrySetValueOnThis(string propertyName, object value)
        {
            bool toReturn = false;
            try
            {
                switch (propertyName)
                {
                    case "AutoGridHorizontalCells":
                        this.AutoGridHorizontalCells = (int)value;
                        break;
                    case "AutoGridVerticalCells":
                        this.AutoGridVerticalCells = (int)value;
                        break;
                    case "Children Layout":
                        this.ChildrenLayout = (ChildrenLayout)value;
                        toReturn = true;
                        break;
                    case "Clips Children":
                        this.ClipsChildren = (bool)value;
                        toReturn = true;
                        break;
                    case "FlipHorizontal":
                        this.FlipHorizontal = (bool)value;
                        toReturn = true;
                        break;
                    case "Height":
                        this.Height = (float)value;
                        toReturn = true;
                        break;
                    case "Height Units":
                        this.HeightUnits = (DimensionUnitType)value;
                        toReturn = true;
                        break;
                    case nameof(IgnoredByParentSize):
                        this.IgnoredByParentSize = (bool)value;
                        toReturn = true;
                        break;
                    case "Parent":
                        {
                            string valueAsString = (string)value;

                            if (!string.IsNullOrEmpty(valueAsString) && mWhatContainsThis != null)
                            {
                                var newParent = this.mWhatContainsThis.GetGraphicalUiElementByName(valueAsString);
                                if (newParent != null)
                                {
                                    Parent = newParent;
                                }
                            }
                            toReturn = true;
                        }
                        break;
                    case "Rotation":
                        this.Rotation = (float)value;
                        toReturn = true;
                        break;
                    case "StackSpacing":
                        this.StackSpacing = (float)value;
                        toReturn = true;
                        break;
                    case "Texture Left":
                        this.TextureLeft = (int)value;
                        toReturn = true;
                        break;
                    case "Texture Top":
                        this.TextureTop = (int)value;
                        toReturn = true;
                        break;
                    case "Texture Width":
                        this.TextureWidth = (int)value;
                        toReturn = true;
                        break;
                    case "Texture Height":
                        this.TextureHeight = (int)value;
                        toReturn = true;

                        break;
                    case "Texture Width Scale":
                        this.TextureWidthScale = (float)value;
                        toReturn = true;
                        break;
                    case "Texture Height Scale":
                        this.TextureHeightScale = (float)value;
                        toReturn = true;
                        break;
                    case "Texture Address":

                        this.TextureAddress = (Gum.Managers.TextureAddress)value;
                        toReturn = true;
                        break;
                    case "Visible":
                        this.Visible = (bool)value;
                        toReturn = true;
                        break;
                    case "Width":
                        this.Width = (float)value;
                        toReturn = true;
                        break;
                    case "Width Units":
                        this.WidthUnits = (DimensionUnitType)value;
                        toReturn = true;
                        break;
                    case "X":
                        this.X = (float)value;
                        toReturn = true;
                        break;
                    case "X Origin":
                        this.XOrigin = (HorizontalAlignment)value;
                        toReturn = true;
                        break;
                    case "X Units":
                        this.XUnits = UnitConverter.ConvertToGeneralUnit(value);
                        toReturn = true;
                        break;
                    case "Y":
                        this.Y = (float)value;
                        toReturn = true;
                        break;
                    case "Y Origin":
                        this.YOrigin = (VerticalAlignment)value;
                        toReturn = true;
                        break;
                    case "Y Units":

                        this.YUnits = UnitConverter.ConvertToGeneralUnit(value);
                        toReturn = true;
                        break;
                    case "Wrap":
                        this.Wrap = (bool)value;
                        toReturn = true;
                        break;
                    case "Wraps Children":
                        this.WrapsChildren = (bool)value;
                        toReturn = true;
                        break;
                }

                if (!toReturn)
                {
                    var propertyNameLength = propertyName.Length;
                    if (propertyNameLength > 5
                        && propertyName[propertyNameLength - 1] == 'e'
                        && propertyName[propertyNameLength - 2] == 't'
                        && propertyName[propertyNameLength - 3] == 'a'
                        && propertyName[propertyNameLength - 4] == 't'
                        && propertyName[propertyNameLength - 5] == 'S'
                        && value is string)
                    {
                        var valueAsString = value as string;

                        string nameWithoutState = propertyName.Substring(0, propertyName.Length - "State".Length);

                        if (string.IsNullOrEmpty(nameWithoutState))
                        {
                            // This is an uncategorized state
                            if (mStates.ContainsKey(valueAsString))
                            {
                                ApplyState(mStates[valueAsString]);
                                toReturn = true;
                            }
                        }
                        else if (mCategories.ContainsKey(nameWithoutState))
                        {

                            var category = mCategories[nameWithoutState];

                            var state = category.States.FirstOrDefault(item => item.Name == valueAsString);
                            if (state != null)
                            {
                                ApplyState(state);
                                toReturn = true;
                            }
                        }
                    }
                }
            }
            catch (InvalidCastException innerException)
            {
                // There could be some rogue value set to the incorrect type, or maybe
                // a new type or plugin initialized the default to the wrong type. We don't
                // want to blow up if this happens
                // Update October 12, 2023
                // This swallowed exception caused
                // problems for myself and arcnor. I 
                // am concerned there may be other exceptions
                // being swallowed, but maybe we should push those
                // errors up and let the callers handle it.
#if DEBUG
                throw new InvalidCastException($"Trying to set property {propertyName} to a value of {value} of type {value?.GetType()} on {Name}", innerException);
#endif
            }
            return toReturn;
        }

        public void ApplyStateRecursive(string categoryName, string stateName)
        {
            if(mCategories.ContainsKey(categoryName))
            {
                var category = mCategories[categoryName];

                var state = category.States.FirstOrDefault(item => item.Name == stateName);
                if (state != null)
                {
                    ApplyState(state);
                }
            }

            if(Children != null)
            {
                foreach (GraphicalUiElement child in this.Children)
                {
                    child.ApplyStateRecursive(categoryName, stateName);
                }

            }
            else
            {
                foreach(var item in this.mWhatThisContains)
                {
                    item.ApplyStateRecursive(categoryName, stateName);
                }
            }
        }

        public void RefreshTextOverflowVerticalMode()
        {

            // we want to let it spill over if it is sized by its children:
            if (this.HeightUnits == DimensionUnitType.RelativeToChildren)
            {
                ((IText)mContainedObjectAsIpso).TextOverflowVerticalMode = TextOverflowVerticalMode.SpillOver;
            }
            else
            {
                ((IText)mContainedObjectAsIpso).TextOverflowVerticalMode = TextOverflowVerticalMode;
            }
        }

        bool useCustomFont;
        public bool UseCustomFont
        {
            get { return useCustomFont; }
            set { useCustomFont = value; UpdateToFontValues(); }
        }

        string customFontFile;
        public string CustomFontFile
        {
            get { return customFontFile; }
            set { customFontFile = value; UpdateToFontValues(); }
        }

        string font;
        public string Font
        {
            get { return font; }
            set { font = value; UpdateToFontValues(); }
        }

        int fontSize;
        public int FontSize
        {
            get { return fontSize; }
            set { fontSize = value; UpdateToFontValues(); }
        }

        bool isItalic;
        public bool IsItalic
        {
            get => isItalic;
            set { isItalic = value; UpdateToFontValues(); }
        }

        bool isBold;
        public bool IsBold
        {
            get => isBold;
            set { isBold = value; UpdateToFontValues(); }
        }

        // Not sure if we need to make this a public value, but we do need to store it
        // Update - yes we do need this to be public so it can be assigned in codegen:
        bool useFontSmoothing = true;
        public bool UseFontSmoothing
        {
            get { return useFontSmoothing; }
            set { useFontSmoothing = value; UpdateToFontValues(); }
        }

        int outlineThickness;
        public int OutlineThickness
        {
            get { return outlineThickness; }
            set { outlineThickness = value; UpdateToFontValues(); }
        }

        public void UpdateFontRecursive()
        {
            if (this.mContainedObjectAsIpso is IText asIText && isFontDirty)
            {

                if(!this.IsLayoutSuspended)
                {
                    UpdateFontFromProperties?.Invoke(asIText, this);
                    isFontDirty = false;
                }
            }

            if (this.Children != null)
            {
                for(int i = 0; i < this.Children.Count; i++)
                {
                    (this.Children[i] as GraphicalUiElement).UpdateFontRecursive();
                }
            }
            else
            {
                for(int i = 0; i < this.mWhatThisContains.Count; i++)
                {
                    mWhatThisContains[i].UpdateFontRecursive();
                }
            }
        }

        public void UpdateToFontValues() => UpdateFontFromProperties?.Invoke(mContainedObjectAsIpso as IText, this);

        #region IVisible Implementation

        bool IVisible.AbsoluteVisible
        {
            get
            {
                bool explicitParentVisible = true;
                if (ExplicitIVisibleParent != null)
                {
                    explicitParentVisible = ExplicitIVisibleParent.AbsoluteVisible;
                }

                return explicitParentVisible && mContainedObjectAsIVisible?.AbsoluteVisible == true;
            }
        }

        IVisible IVisible.Parent
        {
            get { return this.Parent as IVisible; }
        }

        #endregion

        public void ApplyState(string name)
        {
            if (mStates.ContainsKey(name))
            {
                var state = mStates[name];

                ApplyState(state);

            }


            // This is a little dangerous because it's ambiguous.
            // Technically categories could have same-named states.
            foreach (var category in mCategories.Values)
            {
                var foundState = category.States.FirstOrDefault(item => item.Name == name);

                if (foundState != null)
                {
                    ApplyState(foundState);
                }
            }
        }

        public void ApplyState(string categoryName, string stateName)
        {
            if (mCategories.ContainsKey(categoryName))
            {
                var category = mCategories[categoryName];

                var state = category.States.FirstOrDefault(item => item.Name == stateName);

                if (state != null)
                {
                    ApplyState(state);
                }
            }
        }

        public virtual void ApplyState(DataTypes.Variables.StateSave state)
        {
#if DEBUG
            // Dynamic states can be applied in code. It is cumbersome for the user to
            // specify the ParentContainer, especially if the state is to be reused. 
            // I'm removing this to see if it causes problems:
            //if (state.ParentContainer == null)
            //{
            //    throw new InvalidOperationException("State.ParentContainer is null - did you remember to initialize the state?");
            //}

#endif
            if (GraphicalUiElement.IsAllLayoutSuspended == false)
            {
                this.SuspendLayout(true);
            }

            var variablesWithoutStatesOnParent =
                state.Variables.Where(item =>
                {
                    if(item.SetsValue)
                    {
                        // We can set the variable if it's not setting a state (to prevent recursive setting).
                        // Update May 4, 2023 - But if you have a base element that defines a state, and the derived
                        // element sets that state, then we want to allow it.  But should we just allow all states?
                        // Or should we check if it's defined by the base...
                        //return (item.IsState(state.ParentContainer) == false ||
                        //    // If it is setting a state we'll allow it if it's on a child.
                        //    !string.IsNullOrEmpty(item.SourceObject));
                        // let's test this out:
                        return true;

                    }
                    return false;
                }).ToArray();


            var parentSettingVariables =
                variablesWithoutStatesOnParent
                    .Where(item => item.GetRootName() == "Parent")
                    .OrderBy(item => GetOrderedIndexForParentVariable(item))
                    .ToArray();

            var nonParentSettingVariables =
                variablesWithoutStatesOnParent
                    .Except(parentSettingVariables)
                    // Even though we removed state-setting variables on the parent, we still allow setting
                    // states on the contained objects
                    .OrderBy(item => state.ParentContainer == null || !item.IsState(state.ParentContainer))
                    .ToArray();

            var variablesToConsider =
                parentSettingVariables.Concat(nonParentSettingVariables)
                .ToArray();

            int variableCount = variablesToConsider.Length;
            for (int i = 0; i < variableCount; i++)
            {
                var variable = variablesToConsider[i];
                if (variable.SetsValue && variable.Value != null)
                {
                    this.SetProperty(variable.Name, variable.Value);
                }
            }

            foreach (var variableList in state.VariableLists)
            {
                this.SetProperty(variableList.Name, variableList.ValueAsIList);
            }

            if (GraphicalUiElement.IsAllLayoutSuspended == false)
            {
                this.ResumeLayout(true);

            }
        }

        private int GetOrderedIndexForParentVariable(VariableSave item)
        {
            var objectName = item.SourceObject;
            for (int i = 0; i < ElementSave.Instances.Count; i++)
            {
                if (objectName == ElementSave.Instances[i].Name)
                {
                    return i;
                }
            }
            return -1;
        }

        public void ApplyState(List<DataTypes.Variables.VariableSaveValues> variableSaveValues)
        {
            this.SuspendLayout(true);

            foreach (var variable in variableSaveValues)
            {
                if (variable.Value != null)
                {
                    this.SetProperty(variable.Name, variable.Value);
                }
            }
            this.ResumeLayout(true);
        }

        public void AddCategory(DataTypes.Variables.StateSaveCategory category)
        {
            //mCategories[category.Name] = category;
            // Why call "Add"? This makes Gum crash if there are duplicate catgories...
            //mCategories.Add(category.Name, category);
            mCategories[category.Name] = category;
        }

        public void AddStates(List<DataTypes.Variables.StateSave> list)
        {
            foreach (var state in list)
            {
                // Right now this doesn't support inheritance
                // Need to investigate this....at some point:
                mStates[state.Name] = state;
            }
        }

        // When interpolating between two states,
        // the code is goign to merge the values from
        // the two states to create a 3rd set of (merged)
        // values. Interpolation can happen in complex animations
        // resulting in lots of merged lists being created. This allocates
        // tons of memory. Therefore we create a static set of variable lists
        // to store the merged values. We don't know how deep the stack will go
        // (animations within animations) so we need to support a dynamically growing
        // list. The numberOfUsedInterpolationLists stores how many times this is being
        // called so it knows if it needs to add more lists.
        static List<List<Gum.DataTypes.Variables.VariableSaveValues>> listOfListsForReducingAllocInInterpolation = new List<List<Gum.DataTypes.Variables.VariableSaveValues>>();
        int numberOfUsedInterpolationLists = 0;

        public void InterpolateBetween(Gum.DataTypes.Variables.StateSave first, Gum.DataTypes.Variables.StateSave second, float interpolationValue)
        {
            if (numberOfUsedInterpolationLists >= listOfListsForReducingAllocInInterpolation.Count)
            {
                const int capacity = 20;
                var newList = new List<DataTypes.Variables.VariableSaveValues>(capacity);
                listOfListsForReducingAllocInInterpolation.Add(newList);
            }

            List<Gum.DataTypes.Variables.VariableSaveValues> values = listOfListsForReducingAllocInInterpolation[numberOfUsedInterpolationLists];
            values.Clear();
            numberOfUsedInterpolationLists++;

            Gum.DataTypes.Variables.StateSaveExtensionMethods.Merge(first, second, interpolationValue, values);

            this.ApplyState(values);
            numberOfUsedInterpolationLists--;
        }

        #region AnimationChain 


        /// <summary>
        /// Performs AnimationChain (.achx) animation on this and all children recurisvely.
        /// This is typically called on the top-level object (usually Screen) when Gum is running
        /// in a game.
        /// </summary>
        public void AnimateSelf(double secondDifference)
        {
            var asSprite = mContainedObjectAsIpso as ITextureCoordinate;
            var asAnimatable = mContainedObjectAsIpso as IAnimatable;
            //////////////////Early Out/////////////////////
            // Check mContainedObjectAsIVisible - if it's null, then this is a Screen and we should animate it

            // December 6, 2023 - Not sure why this was added here
            // but by checking if this is null, we skip animating screens
            // which breaks recursive animations. We need to early out only
            // if the contained object is not null.
            //if(asSprite== null || asAnimatable == null)
            //{
            //    return;
            //}

            if (mContainedObjectAsIVisible != null && Visible == false)
            {
                return;
            }
            ////////////////End Early Out///////////////////


            var didSpriteUpdate = asAnimatable?.AnimateSelf(secondDifference) ?? false;

            if(didSpriteUpdate)
            {
                // update this texture coordinates:

                UpdateTextureValuesFrom(asSprite);
            }

            if (Children != null)
            {
                for(int i = 0; i < this.Children.Count; i++)
                {
                    var child = this.Children[i];
                    if (child is GraphicalUiElement childGue)
                    {
                        childGue.AnimateSelf(secondDifference);
                    }
                }
            }
            else
            {
                for(int i = 0; i < this.mWhatThisContains.Count; i++)
                {
                    var child = mWhatThisContains[i];
                    if (child is GraphicalUiElement childGue)
                    {
                        childGue.AnimateSelf(secondDifference);
                    }
                }
            }
        }

        public void UpdateTextureValuesFrom(ITextureCoordinate asSprite)
        {
            // suspend layouts while we do this so that previou values don't apply:
            var isSuspended = this.IsLayoutSuspended;
            this.SuspendLayout();

            // The AnimationChain (source file) could get set before the name desired name is set, so tolerate 
            // if there's a missing source rectangle:
            if(asSprite.SourceRectangle != null)
            {
                this.TextureLeft = asSprite.SourceRectangle.Value.Left;
                this.TextureWidth = asSprite.SourceRectangle.Value.Width;

                this.TextureTop = asSprite.SourceRectangle.Value.Top;
                this.TextureHeight = asSprite.SourceRectangle.Value.Height;
            }

            this.FlipHorizontal = asSprite.FlipHorizontal;

            if (this.TextureAddress == TextureAddress.EntireTexture)
            {
                this.TextureAddress = TextureAddress.Custom; // If it's not custom, then the animation chain won't apply. I think we should force this.
            }
            if(isSuspended == false)
            {
                this.ResumeLayout();
            }
        }

        #endregion


        public bool IsPointInside(float x, float y)
        {
            var asIpso = this as IRenderableIpso;

            var absoluteX = asIpso.GetAbsoluteX();
            var absoluteY = asIpso.GetAbsoluteY();

            return
                x > absoluteX &&
                y > absoluteY &&
                x < absoluteX + this.GetAbsoluteWidth() &&
                y < absoluteY + this.GetAbsoluteHeight();
        }

#endregion
    }

    // additional interfaces, added here to make it easier to manage multiple projects.
    public interface IManagedObject
    {
        void AddToManagers();
        void RemoveFromManagers();
    }
}
